using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    // uiapp 은 Revit 을 쓰지 않는 도구(로컬 저장소 계열)에서는 null 이다.
    internal delegate object ToolHandler(UIApplication uiapp, JObject args);

    internal sealed class ToolDef
    {
        public string Name;
        public string Description;
        public JObject InputSchema;
        public ToolHandler Handler;
        public bool NeedsRevit = true;
    }

    internal static class ToolRegistry
    {
        static readonly List<ToolDef> _tools = new List<ToolDef>();
        static readonly Dictionary<string, ToolDef> _byName =
            new Dictionary<string, ToolDef>(StringComparer.OrdinalIgnoreCase);

        public static int Count { get { return _tools.Count; } }

        public static void Register(string name, string description, S schema, ToolHandler handler)
        {
            Register(name, description, schema, handler, true);
        }

        public static void Register(string name, string description, S schema, ToolHandler handler, bool needsRevit)
        {
            ToolDef d = new ToolDef();
            d.Name = name;
            d.Description = description;
            d.InputSchema = schema.Build();
            d.Handler = handler;
            d.NeedsRevit = needsRevit;

            if (_byName.ContainsKey(name))
            {
                Log.Warn("도구 이름이 중복되어 나중 것으로 덮어씁니다: " + name);
                _tools.RemoveAll(delegate(ToolDef t) { return string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase); });
            }
            _tools.Add(d);
            _byName[name] = d;
        }

        public static JArray ListSchema()
        {
            JArray arr = new JArray();
            foreach (ToolDef d in _tools)
            {
                arr.Add(new JObject(
                    new JProperty("name", d.Name),
                    new JProperty("description", d.Description),
                    new JProperty("inputSchema", d.InputSchema)));
            }
            return arr;
        }

        public static JObject Call(JObject callParams)
        {
            string name = A.Str(callParams, "name", null);
            if (string.IsNullOrEmpty(name))
                return TextResult("도구 이름(name)이 없습니다.", true);

            ToolDef def;
            if (!_byName.TryGetValue(name, out def))
                return TextResult("그런 도구가 없습니다: " + name, true);

            JObject args = A.Obj(callParams, "arguments");
            if (args == null) args = new JObject();

            int timeout = Config.ClampTimeout(A.Has(args, "timeoutMs") ? (int?)A.Int(args, "timeoutMs", 0) : null);

            try
            {
                object result;
                if (def.NeedsRevit)
                {
                    ToolDef captured = def;
                    JObject capturedArgs = args;
                    result = Dispatcher.Invoke(
                        delegate(UIApplication uiapp) { return captured.Handler(uiapp, capturedArgs); },
                        timeout);
                }
                else
                {
                    result = def.Handler(null, args);
                }
                return TextResult(Stringify(result), false);
            }
            catch (Exception ex)
            {
                Log.Error("도구 실행 실패: " + name, ex);
                string msg = ex.GetType().Name + ": " + ex.Message;
                if (ex.InnerException != null)
                    msg += " -> " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
                return TextResult("[" + name + "] 실행 실패\n" + msg, true);
            }
        }

        static string Stringify(object result)
        {
            if (result == null) return "(결과 없음)";
            string s = result as string;
            if (s != null) return s;
            JToken t = result as JToken;
            if (t != null) return t.ToString(Formatting.Indented);
            return JsonConvert.SerializeObject(result, Formatting.Indented);
        }

        static JObject TextResult(string text, bool isError)
        {
            JArray content = new JArray();
            content.Add(new JObject(
                new JProperty("type", "text"),
                new JProperty("text", text)));

            return new JObject(
                new JProperty("content", content),
                new JProperty("isError", isError));
        }
    }

    // 트랜잭션과 결과 조립을 위한 공통 도우미.
    internal static class Rx
    {
        public static Document Doc(UIApplication uiapp)
        {
            UIDocument uidoc = uiapp != null ? uiapp.ActiveUIDocument : null;
            if (uidoc == null || uidoc.Document == null)
                throw new InvalidOperationException("열려 있는 Revit 문서가 없습니다.");
            return uidoc.Document;
        }

        public static UIDocument UiDoc(UIApplication uiapp)
        {
            UIDocument uidoc = uiapp != null ? uiapp.ActiveUIDocument : null;
            if (uidoc == null) throw new InvalidOperationException("열려 있는 Revit 문서가 없습니다.");
            return uidoc;
        }

        public static T Tx<T>(Document doc, string name, Func<T> body)
        {
            using (Transaction t = new Transaction(doc, name))
            {
                t.Start();
                try
                {
                    T r = body();
                    t.Commit();
                    return r;
                }
                catch
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    throw;
                }
            }
        }

        public static JObject Ok()
        {
            return new JObject(new JProperty("success", true));
        }

        public static JObject Ok(string message)
        {
            return new JObject(
                new JProperty("success", true),
                new JProperty("message", message));
        }

        // 요소 하나를 요약해 돌려준다. 좌표는 mm.
        public static JObject Describe(Document doc, Element e, bool withParameters)
        {
            JObject o = new JObject();
            o["id"] = e.Id.IntegerValue;
            o["name"] = e.Name;
            o["category"] = e.Category != null ? e.Category.Name : null;
            o["class"] = e.GetType().Name;

            ElementId typeId = e.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                Element te = doc.GetElement(typeId);
                if (te != null)
                {
                    o["typeId"] = typeId.IntegerValue;
                    o["typeName"] = te.Name;
                    ElementType et = te as ElementType;
                    if (et != null) o["familyName"] = et.FamilyName;
                }
            }

            if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId)
            {
                Element lv = doc.GetElement(e.LevelId);
                if (lv != null) o["level"] = lv.Name;
            }

            try
            {
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb != null)
                {
                    o["boundingBox"] = new JObject(
                        new JProperty("min", PtJson(bb.Min)),
                        new JProperty("max", PtJson(bb.Max)));
                }
            }
            catch { }

            try
            {
                LocationPoint lp = e.Location as LocationPoint;
                if (lp != null) o["location"] = PtJson(lp.Point);

                LocationCurve lc = e.Location as LocationCurve;
                if (lc != null && lc.Curve != null)
                {
                    o["start"] = PtJson(lc.Curve.GetEndPoint(0));
                    o["end"] = PtJson(lc.Curve.GetEndPoint(1));
                    o["length"] = Math.Round(U.ToMm(lc.Curve.Length), 2);
                }
            }
            catch { }

            if (withParameters)
            {
                JObject ps = new JObject();
                foreach (Parameter p in e.Parameters)
                {
                    try
                    {
                        if (p == null || p.Definition == null) continue;
                        string key = p.Definition.Name;
                        if (ps[key] != null) continue;
                        ps[key] = ParamValue(p);
                    }
                    catch { }
                }
                o["parameters"] = ps;
            }
            return o;
        }

        public static JToken ParamValue(Parameter p)
        {
            if (!p.HasValue) return JValue.CreateNull();
            switch (p.StorageType)
            {
                case StorageType.Double:
                    // 길이 계열이면 mm 로 바꿔서 보여준다.
                    try
                    {
                        double raw = p.AsDouble();
                        ForgeTypeId spec = p.Definition.GetDataType();
                        if (spec != null && spec.Equals(SpecTypeId.Length))
                            return Math.Round(U.ToMm(raw), 3);
                        return Math.Round(raw, 6);
                    }
                    catch { return p.AsDouble(); }
                case StorageType.Integer:
                    return p.AsInteger();
                case StorageType.String:
                    return p.AsString();
                case StorageType.ElementId:
                    return p.AsElementId().IntegerValue;
                default:
                    return p.AsValueString();
            }
        }

        public static JObject PtJson(XYZ ptFt)
        {
            if (ptFt == null) return null;
            return new JObject(
                new JProperty("x", Math.Round(U.ToMm(ptFt.X), 2)),
                new JProperty("y", Math.Round(U.ToMm(ptFt.Y), 2)),
                new JProperty("z", Math.Round(U.ToMm(ptFt.Z), 2)));
        }

        // "OST_Walls" 또는 "Walls" 또는 "벽" 을 BuiltInCategory 로 푼다.
        public static BuiltInCategory ResolveCategory(Document doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return BuiltInCategory.INVALID;

            BuiltInCategory bic;
            if (System.Enum.TryParse<BuiltInCategory>(name, true, out bic)) return bic;
            if (System.Enum.TryParse<BuiltInCategory>("OST_" + name, true, out bic)) return bic;

            // 표시 이름으로 찾는다 (한글 UI 포함).
            foreach (Category c in doc.Settings.Categories)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    try { return (BuiltInCategory)c.Id.IntegerValue; }
                    catch { }
                }
            }
            return BuiltInCategory.INVALID;
        }
    }
}
