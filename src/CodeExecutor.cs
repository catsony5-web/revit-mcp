using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    // 사용자 C# 코드를 컴파일해서 Revit UI 스레드에서 실행한다.
    // Roslyn 이 있으면 최신 C# 을, 없으면 CodeDom(C# 5)으로 떨어진다.
    internal static class CodeExecutor
    {
        static readonly Dictionary<string, MethodInfo> _cache = new Dictionary<string, MethodInfo>();
        static readonly object _cacheGate = new object();
        static bool? _roslynOk;

        public const string TemplateHeader =
            "using System;\n" +
            "using System.Collections;\n" +
            "using System.Collections.Generic;\n" +
            "using System.Linq;\n" +
            "using System.Text;\n" +
            "using Autodesk.Revit.DB;\n" +
            "using Autodesk.Revit.DB.Architecture;\n" +
            "using Autodesk.Revit.DB.Structure;\n" +
            "using Autodesk.Revit.DB.Mechanical;\n" +
            "using Autodesk.Revit.DB.Plumbing;\n" +
            "using Autodesk.Revit.DB.Electrical;\n" +
            "using Autodesk.Revit.UI;\n" +
            "using Autodesk.Revit.UI.Selection;\n" +
            "using Newtonsoft.Json;\n" +
            "using Newtonsoft.Json.Linq;\n";

        static string BuildSource(string userCode)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(TemplateHeader);
            sb.Append("public class McpScript\n{\n");
            sb.Append("    public static object Execute(UIApplication uiapp, UIDocument uidoc, Document document, ");
            sb.Append("Autodesk.Revit.ApplicationServices.Application app, object[] parameters)\n    {\n");
            sb.Append("        Document doc = document;\n");
            sb.Append("#line 1 \"user\"\n");
            sb.Append(userCode);
            sb.Append("\n#line default\n");
            sb.Append("        return null;\n");
            sb.Append("    }\n}\n");
            return sb.ToString();
        }

        static List<string> ReferencePaths()
        {
            List<string> paths = new List<string>();
            Action<Assembly> add = delegate(Assembly asm)
            {
                try
                {
                    if (asm != null && !asm.IsDynamic && !string.IsNullOrEmpty(asm.Location) &&
                        !paths.Contains(asm.Location) && File.Exists(asm.Location))
                        paths.Add(asm.Location);
                }
                catch { }
            };

            // 이미 Revit 프로세스에 로드된 어셈블리를 그대로 참조하면 버전 충돌이 없다.
            add(typeof(object).Assembly);                 // mscorlib
            add(typeof(Uri).Assembly);                    // System
            add(typeof(System.Linq.Enumerable).Assembly); // System.Core
            add(typeof(System.Data.DataTable).Assembly);  // System.Data
            add(typeof(System.Xml.XmlDocument).Assembly); // System.Xml
            add(typeof(Document).Assembly);               // RevitAPI
            add(typeof(UIApplication).Assembly);          // RevitAPIUI
            add(typeof(JObject).Assembly);                // Newtonsoft.Json
            return paths;
        }

        public static object Run(UIApplication uiapp, string userCode, JArray parameters, string transactionMode, string txName)
        {
            if (string.IsNullOrWhiteSpace(userCode))
                throw new ArgumentException("실행할 코드(code)가 비어 있습니다.");

            MethodInfo method = GetCompiled(userCode);

            UIDocument uidoc = uiapp != null ? uiapp.ActiveUIDocument : null;
            Document doc = uidoc != null ? uidoc.Document : null;
            Autodesk.Revit.ApplicationServices.Application app = uiapp != null ? uiapp.Application : null;

            object[] pars = ToObjectArray(parameters);
            object[] callArgs = new object[] { uiapp, uidoc, doc, app, pars };

            string mode = string.IsNullOrEmpty(transactionMode) ? "auto" : transactionMode.ToLowerInvariant();

            if (mode == "none" || mode == "manual")
            {
                // manual: 코드가 직접 트랜잭션을 연다. 대량 작업을 나눠 커밋해 전체 롤백을 피할 때 쓴다.
                return Unwrap(method, callArgs);
            }

            if (doc == null) throw new InvalidOperationException("열려 있는 Revit 문서가 없습니다.");
            string name = string.IsNullOrEmpty(txName) ? "MCP 코드 실행" : txName;
            return Rx.Tx<object>(doc, name, delegate { return Unwrap(method, callArgs); });
        }

        static object Unwrap(MethodInfo method, object[] callArgs)
        {
            try { return method.Invoke(null, callArgs); }
            catch (TargetInvocationException tie)
            {
                // 사용자 코드가 던진 진짜 예외를 그대로 올린다.
                if (tie.InnerException != null) throw tie.InnerException;
                throw;
            }
        }

        static object[] ToObjectArray(JArray parameters)
        {
            if (parameters == null) return new object[0];
            object[] arr = new object[parameters.Count];
            for (int i = 0; i < parameters.Count; i++)
            {
                JToken t = parameters[i];
                JValue v = t as JValue;
                arr[i] = v != null ? v.Value : (object)t;
            }
            return arr;
        }

        static MethodInfo GetCompiled(string userCode)
        {
            string key = Hash(userCode);
            lock (_cacheGate)
            {
                MethodInfo cached;
                if (_cache.TryGetValue(key, out cached)) return cached;
            }

            string source = BuildSource(userCode);
            Assembly asm = Compile(source);

            Type t = asm.GetType("McpScript");
            if (t == null) throw new InvalidOperationException("컴파일 결과에서 McpScript 형식을 찾지 못했습니다.");
            MethodInfo m = t.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
            if (m == null) throw new InvalidOperationException("컴파일 결과에서 Execute 메서드를 찾지 못했습니다.");

            lock (_cacheGate)
            {
                if (_cache.Count > 200) _cache.Clear();
                _cache[key] = m;
            }
            return m;
        }

        static Assembly Compile(string source)
        {
            if (_roslynOk != false)
            {
                try
                {
                    Assembly a = RoslynCompiler.Compile(source, ReferencePaths());
                    _roslynOk = true;
                    return a;
                }
                catch (CompileErrorException) { throw; }   // 문법 오류는 그대로 사용자에게
                catch (Exception ex)
                {
                    // Roslyn 자체를 못 불러온 경우에만 대체 경로로 간다.
                    if (_roslynOk == null)
                    {
                        _roslynOk = false;
                        Log.Warn("Roslyn 을 쓸 수 없어 CodeDom(C# 5)으로 대체합니다: " + ex.GetType().Name + ": " + ex.Message);
                    }
                    else throw;
                }
            }
            return CodeDomCompiler.Compile(source, ReferencePaths());
        }

        public static string ActiveCompiler
        {
            get
            {
                if (_roslynOk == true) return "Roslyn (최신 C#)";
                if (_roslynOk == false) return "CodeDom (C# 5)";
                return "(아직 컴파일한 적 없음)";
            }
        }

        static string Hash(string s)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] b = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                return BitConverter.ToString(b).Replace("-", "");
            }
        }
    }

    internal sealed class CompileErrorException : Exception
    {
        public CompileErrorException(string message) : base(message) { }
    }

    // Roslyn 은 별도 클래스에 가둔다. DLL 이 없으면 이 클래스를 처음 건드릴 때만 터지고,
    // 호출부에서 잡아 CodeDom 으로 넘어갈 수 있다.
    internal static class RoslynCompiler
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Assembly Compile(string source, List<string> referencePaths)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(source);

            List<MetadataReference> refs = new List<MetadataReference>();
            foreach (string p in referencePaths)
            {
                try { refs.Add(MetadataReference.CreateFromFile(p)); }
                catch { }
            }

            CSharpCompilationOptions options = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release);

            CSharpCompilation comp = CSharpCompilation.Create(
                "McpScript_" + Guid.NewGuid().ToString("N"),
                new SyntaxTree[] { tree },
                refs,
                options);

            using (MemoryStream ms = new MemoryStream())
            {
                EmitResult result = comp.Emit(ms);
                if (!result.Success)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("컴파일 오류:");
                    foreach (Diagnostic d in result.Diagnostics)
                    {
                        if (d.Severity != DiagnosticSeverity.Error) continue;
                        FileLinePositionSpan span = d.Location.GetLineSpan();
                        sb.Append("  줄 ").Append(span.StartLinePosition.Line + 1)
                          .Append(": ").Append(d.Id).Append(" ").AppendLine(d.GetMessage());
                    }
                    throw new CompileErrorException(sb.ToString());
                }
                return Assembly.Load(ms.ToArray());
            }
        }
    }

    // Roslyn 이 없을 때의 대체 경로. C# 5 문법만 된다.
    internal static class CodeDomCompiler
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Assembly Compile(string source, List<string> referencePaths)
        {
            Microsoft.CSharp.CSharpCodeProvider provider = new Microsoft.CSharp.CSharpCodeProvider();
            System.CodeDom.Compiler.CompilerParameters cp = new System.CodeDom.Compiler.CompilerParameters();
            cp.GenerateInMemory = true;
            cp.GenerateExecutable = false;
            cp.TreatWarningsAsErrors = false;
            foreach (string p in referencePaths) cp.ReferencedAssemblies.Add(p);

            System.CodeDom.Compiler.CompilerResults results = provider.CompileAssemblyFromSource(cp, source);
            if (results.Errors.HasErrors)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("컴파일 오류 (C# 5 대체 컴파일러):");
                foreach (System.CodeDom.Compiler.CompilerError err in results.Errors)
                {
                    if (err.IsWarning) continue;
                    sb.Append("  줄 ").Append(err.Line).Append(": ")
                      .Append(err.ErrorNumber).Append(" ").AppendLine(err.ErrorText);
                }
                throw new CompileErrorException(sb.ToString());
            }
            return results.CompiledAssembly;
        }
    }
}
