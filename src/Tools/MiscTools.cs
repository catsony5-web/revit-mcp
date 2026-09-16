using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    internal static class MiscTools
    {
        public static void Register()
        {
            ToolRegistry.Register("say_hello",
                "서버와 Revit 사이의 연결을 점검한다. Revit 버전, 열려 있는 문서, 활성 뷰, 등록된 도구 수, " +
                "사용 중인 컴파일러를 돌려준다. 무언가 안 될 때 가장 먼저 부르는 도구다.",
                S.Obj().Str("message", "함께 메아리쳐 돌려받을 문자열 (선택)"),
                SayHello);

            ToolRegistry.Register("send_code_to_revit",
                "C# 코드를 Revit 안에서 직접 실행한다. 다른 도구로 표현하기 어려운 작업은 전부 이걸로 한다.\n" +
                "코드는 메서드 본문에 그대로 들어가며 다음 변수를 쓸 수 있다: " +
                "document (Document, 별칭 doc), uidoc (UIDocument), uiapp (UIApplication), " +
                "app (Application), parameters (object[]).\n" +
                "값을 돌려주려면 return 문을 쓴다. 문자열, 숫자, 익명 객체, JObject 모두 된다.\n" +
                "기본 transactionMode 는 auto 이며 트랜잭션이 이미 열린 상태로 코드가 시작된다 " +
                "(직접 Transaction 을 열면 안 된다).\n" +
                "대량 변경은 manual 을 써서 코드 안에서 트랜잭션을 여러 번 나눠 커밋하십시오. " +
                "auto 로 한 번에 처리하면 도중 한 건이 실패할 때 전체가 롤백됩니다.",
                S.Obj()
                    .StrReq("code", "실행할 C# 코드. 메서드 본문에 그대로 삽입된다. using 문은 이미 주요 Revit 네임스페이스가 걸려 있다.")
                    .AnyArr("parameters", "코드에서 parameters[0] 처럼 꺼내 쓸 인자 배열 (선택)")
                    .Enum("transactionMode",
                          "auto = 트랜잭션을 열어주고 끝나면 커밋한다 (기본). " +
                          "manual = 코드가 직접 트랜잭션을 관리한다. 대량 작업을 나눠 커밋할 때 쓴다. " +
                          "none = 트랜잭션 없이 읽기만 한다.",
                          new string[] { "auto", "manual", "none" }, "auto")
                    .Str("transactionName", "실행 취소 목록에 남길 트랜잭션 이름 (auto 일 때만 쓰인다)")
                    .Int("timeoutMs", "이 호출의 제한 시간(밀리초). 오래 걸리는 작업이면 늘린다. 상한은 서버 설정을 따른다."),
                SendCodeToRevit);
        }

        static object SayHello(UIApplication uiapp, JObject args)
        {
            JObject o = new JObject();
            o["ok"] = true;
            o["server"] = McpProtocol.ServerName + " " + McpProtocol.ServerVersion;
            o["toolCount"] = ToolRegistry.Count;
            o["codeCompiler"] = CodeExecutor.ActiveCompiler;
            o["logDirectory"] = Log.Directory;

            if (uiapp != null)
            {
                try
                {
                    o["revitVersion"] = uiapp.Application.VersionNumber;
                    o["revitBuild"] = uiapp.Application.VersionBuild;
                    o["username"] = uiapp.Application.Username;
                }
                catch { }

                UIDocument uidoc = uiapp.ActiveUIDocument;
                if (uidoc != null && uidoc.Document != null)
                {
                    o["documentTitle"] = uidoc.Document.Title;
                    o["documentPath"] = uidoc.Document.PathName;
                    o["isModified"] = uidoc.Document.IsModified;
                    View v = uidoc.Document.ActiveView;
                    if (v != null)
                    {
                        o["activeView"] = v.Name;
                        o["activeViewType"] = v.ViewType.ToString();
                    }
                }
                else
                {
                    o["documentTitle"] = null;
                    o["warning"] = "Revit 은 떠 있지만 열려 있는 문서가 없습니다.";
                }
            }

            string echo = A.Str(args, "message", null);
            if (!string.IsNullOrEmpty(echo)) o["echo"] = echo;
            return o;
        }

        static object SendCodeToRevit(UIApplication uiapp, JObject args)
        {
            string code = A.StrReq(args, "code");
            JArray pars = A.Arr(args, "parameters");
            string mode = A.Str(args, "transactionMode", "auto");
            string txName = A.Str(args, "transactionName", null);

            DateTime t0 = DateTime.Now;
            object result = CodeExecutor.Run(uiapp, code, pars, mode, txName);
            double ms = (DateTime.Now - t0).TotalMilliseconds;

            JObject o = new JObject();
            o["success"] = true;
            o["transactionMode"] = mode;
            o["elapsedMs"] = Math.Round(ms, 1);
            o["compiler"] = CodeExecutor.ActiveCompiler;
            o["result"] = ToToken(result);
            return o;
        }

        // 사용자 코드가 무엇을 돌려주든 JSON 으로 바꾼다.
        static JToken ToToken(object result)
        {
            if (result == null) return JValue.CreateNull();
            JToken t = result as JToken;
            if (t != null) return t;
            try { return JToken.FromObject(result); }
            catch { return new JValue(result.ToString()); }
        }
    }
}
