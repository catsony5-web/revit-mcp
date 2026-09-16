using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    // JSON Schema 를 짧게 쓰기 위한 빌더. C# 5 라서 인덱스 초기화자를 못 쓰므로 유창식으로 만든다.
    internal sealed class S
    {
        readonly JObject _props = new JObject();
        readonly JArray _required = new JArray();

        public static S Obj() { return new S(); }

        S Add(string name, JObject def, bool required)
        {
            _props[name] = def;
            if (required) _required.Add(name);
            return this;
        }

        static JObject Base(string type, string desc)
        {
            JObject o = new JObject();
            o["type"] = type;
            if (!string.IsNullOrEmpty(desc)) o["description"] = desc;
            return o;
        }

        public S Str(string name, string desc) { return Add(name, Base("string", desc), false); }
        public S StrReq(string name, string desc) { return Add(name, Base("string", desc), true); }

        public S StrDef(string name, string desc, string def)
        {
            JObject o = Base("string", desc);
            o["default"] = def;
            return Add(name, o, false);
        }

        public S Enum(string name, string desc, string[] values, string def)
        {
            JObject o = Base("string", desc);
            o["enum"] = new JArray(values);
            if (def != null) o["default"] = def;
            return Add(name, o, false);
        }

        public S Num(string name, string desc) { return Add(name, Base("number", desc), false); }
        public S NumReq(string name, string desc) { return Add(name, Base("number", desc), true); }

        public S NumDef(string name, string desc, double def)
        {
            JObject o = Base("number", desc);
            o["default"] = def;
            return Add(name, o, false);
        }

        public S Int(string name, string desc) { return Add(name, Base("integer", desc), false); }
        public S IntReq(string name, string desc) { return Add(name, Base("integer", desc), true); }

        public S IntDef(string name, string desc, int def)
        {
            JObject o = Base("integer", desc);
            o["default"] = def;
            return Add(name, o, false);
        }

        public S Bool(string name, string desc, bool def)
        {
            JObject o = Base("boolean", desc);
            o["default"] = def;
            return Add(name, o, false);
        }

        public S IntArr(string name, string desc, bool required)
        {
            JObject o = Base("array", desc);
            o["items"] = Base("integer", null);
            return Add(name, o, required);
        }

        public S StrArr(string name, string desc, bool required)
        {
            JObject o = Base("array", desc);
            o["items"] = Base("string", null);
            return Add(name, o, required);
        }

        public S ObjArr(string name, string desc, S item, bool required)
        {
            JObject o = Base("array", desc);
            o["items"] = item.Build();
            return Add(name, o, required);
        }

        public S Sub(string name, string desc, S sub, bool required)
        {
            JObject o = sub.Build();
            if (!string.IsNullOrEmpty(desc)) o["description"] = desc;
            return Add(name, o, required);
        }

        public S Any(string name, string desc)
        {
            JObject o = new JObject();
            if (!string.IsNullOrEmpty(desc)) o["description"] = desc;
            return Add(name, o, false);
        }

        public S AnyArr(string name, string desc)
        {
            JObject o = Base("array", desc);
            o["items"] = new JObject();
            return Add(name, o, false);
        }

        // {x, y, z} mm 점. 아주 자주 쓰여서 따로 뺀다.
        public static S Point(string what)
        {
            return Obj()
                .NumReq("x", what + " X 좌표 (mm)")
                .NumReq("y", what + " Y 좌표 (mm)")
                .NumReq("z", what + " Z 좌표 (mm)");
        }

        public S Pt(string name, string desc, bool required)
        {
            return Sub(name, desc, Point(desc), required);
        }

        public JObject Build()
        {
            JObject o = new JObject();
            o["type"] = "object";
            o["properties"] = _props;
            if (_required.Count > 0) o["required"] = _required;
            return o;
        }
    }

    // 인자 읽기 도우미. 값이 없거나 형이 다르면 기본값으로 떨어진다.
    internal static class A
    {
        public static bool Has(JObject o, string k)
        {
            return o != null && o[k] != null && o[k].Type != JTokenType.Null;
        }

        public static string Str(JObject o, string k, string def)
        {
            return Has(o, k) ? o[k].ToString() : def;
        }

        public static string StrReq(JObject o, string k)
        {
            if (!Has(o, k)) throw new ArgumentException("필수 인자가 없습니다: " + k);
            return o[k].ToString();
        }

        public static double Num(JObject o, string k, double def)
        {
            if (!Has(o, k)) return def;
            try { return o[k].Value<double>(); } catch { return def; }
        }

        public static double NumReq(JObject o, string k)
        {
            if (!Has(o, k)) throw new ArgumentException("필수 인자가 없습니다: " + k);
            return o[k].Value<double>();
        }

        public static int Int(JObject o, string k, int def)
        {
            if (!Has(o, k)) return def;
            try { return o[k].Value<int>(); } catch { return def; }
        }

        public static int IntReq(JObject o, string k)
        {
            if (!Has(o, k)) throw new ArgumentException("필수 인자가 없습니다: " + k);
            return o[k].Value<int>();
        }

        public static bool Bool(JObject o, string k, bool def)
        {
            if (!Has(o, k)) return def;
            try { return o[k].Value<bool>(); } catch { return def; }
        }

        public static JObject Obj(JObject o, string k)
        {
            return Has(o, k) ? o[k] as JObject : null;
        }

        public static JArray Arr(JObject o, string k)
        {
            return Has(o, k) ? o[k] as JArray : null;
        }

        // {x,y,z} (mm) -> Revit 내부 좌표(ft)
        public static XYZ PointFt(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            JObject o = t as JObject;
            if (o == null) return null;
            return U.PtFt(Num(o, "x", 0), Num(o, "y", 0), Num(o, "z", 0));
        }

        public static XYZ PointFtReq(JObject parent, string key)
        {
            XYZ p = PointFt(parent != null ? parent[key] : null);
            if (p == null) throw new ArgumentException("필수 좌표 인자가 없습니다: " + key);
            return p;
        }

        public static List<ElementId> ElementIds(JObject o, string k)
        {
            List<ElementId> ids = new List<ElementId>();
            JArray arr = Arr(o, k);
            if (arr == null) return ids;
            foreach (JToken t in arr)
            {
                try { ids.Add(new ElementId(t.Value<int>())); }
                catch { }
            }
            return ids;
        }
    }
}
