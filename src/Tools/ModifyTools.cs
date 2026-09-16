using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    internal static class ModifyTools
    {
        public static void Register()
        {
            ToolRegistry.Register("operate_element",
                "요소를 화면에서 다룬다. 선택, 숨기기, 숨김 해제, 임시 분리, 분리 해제, 확대, 삭제를 지원한다. " +
                "숨기기·분리는 현재 뷰에만 적용된다.",
                S.Obj()
                    .IntArr("elementIds", "대상 요소의 ElementId 목록", true)
                    .Enum("action", "수행할 동작",
                          new string[] { "select", "hide", "unhide", "isolate", "resetIsolate", "zoom", "delete" },
                          "select"),
                OperateElement);

            ToolRegistry.Register("modify_element",
                "이미 있는 요소를 고친다. 파라미터 값 변경, 이동, 회전, 유형 교체를 한 번에 할 수 있다. " +
                "길이 계열 파라미터와 이동량은 mm 로 준다.",
                S.Obj()
                    .ObjArr("elements", "고칠 요소 목록",
                        S.Obj()
                            .IntReq("elementId", "대상 요소의 ElementId")
                            .Any("parameters", "바꿀 파라미터. {\"파라미터이름\": 값} 형태의 객체. 길이는 mm.")
                            .Pt("move", "이 만큼 이동시킨다 (mm). 상대 이동량이다.", false)
                            .Num("rotate", "Z축 기준 회전 각도 (도). 요소의 위치점을 중심으로 돈다.")
                            .Int("newTypeId", "이 유형으로 교체한다. ElementType 의 ElementId."),
                        true),
                ModifyElement);

            ToolRegistry.Register("delete_element",
                "요소를 모델에서 지운다. 되돌릴 수 없는 변경이므로 지울 대상이 맞는지 먼저 확인한다. " +
                "부속을 지우면 연결된 요소가 함께 지워질 수 있다.",
                S.Obj()
                    .IntArr("elementIds", "지울 요소의 ElementId 목록", true),
                DeleteElement);

            ToolRegistry.Register("color_elements",
                "현재 뷰에서 요소를 파라미터 값에 따라 색으로 구분한다. 값이 같은 것끼리 같은 색을 받는다. " +
                "검토용 색분류에 쓴다.",
                S.Obj()
                    .StrReq("categoryName", "색을 입힐 카테고리. 예: 'Walls', '벽', 'OST_Doors'")
                    .StrReq("parameterName", "묶음 기준이 될 파라미터 이름")
                    .Bool("useGradient", "무작위 색 대신 색상 띠를 따라 순서대로 칠할지", false)
                    .ObjArr("customColors", "쓰고 싶은 색을 직접 지정한다 (선택). 값 순서대로 배정된다.",
                        S.Obj().IntReq("r", "0-255").IntReq("g", "0-255").IntReq("b", "0-255"), false),
                ColorElements);
        }

        static object OperateElement(UIApplication uiapp, JObject args)
        {
            UIDocument uidoc = Rx.UiDoc(uiapp);
            Document doc = uidoc.Document;
            View view = doc.ActiveView;

            List<ElementId> ids = A.ElementIds(args, "elementIds");
            string action = A.Str(args, "action", "select").ToLowerInvariant();

            if (ids.Count == 0 && action != "resetisolate")
                throw new ArgumentException("elementIds 가 비어 있습니다.");

            // 실제로 있는 요소만 남긴다.
            List<ElementId> valid = new List<ElementId>();
            List<int> missing = new List<int>();
            foreach (ElementId id in ids)
            {
                if (doc.GetElement(id) != null) valid.Add(id);
                else missing.Add(id.IntegerValue);
            }

            JObject res = new JObject();
            res["action"] = action;
            res["requested"] = ids.Count;
            res["affected"] = valid.Count;
            if (missing.Count > 0) res["notFound"] = new JArray(missing.ToArray());

            switch (action)
            {
                case "select":
                    uidoc.Selection.SetElementIds(valid);
                    res["success"] = true;
                    break;

                case "zoom":
                    uidoc.Selection.SetElementIds(valid);
                    uidoc.ShowElements(valid);
                    res["success"] = true;
                    break;

                case "hide":
                    Rx.Tx<object>(doc, "요소 숨기기", delegate
                    {
                        view.HideElements(valid);
                        return null;
                    });
                    res["success"] = true;
                    break;

                case "unhide":
                    Rx.Tx<object>(doc, "숨김 해제", delegate
                    {
                        view.UnhideElements(valid);
                        return null;
                    });
                    res["success"] = true;
                    break;

                case "isolate":
                    Rx.Tx<object>(doc, "임시 분리", delegate
                    {
                        view.IsolateElementsTemporary(valid);
                        return null;
                    });
                    res["success"] = true;
                    break;

                case "resetisolate":
                    Rx.Tx<object>(doc, "분리 해제", delegate
                    {
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        return null;
                    });
                    res["success"] = true;
                    break;

                case "delete":
                    ICollection<ElementId> deleted = Rx.Tx<ICollection<ElementId>>(doc, "요소 삭제", delegate
                    {
                        return doc.Delete(valid);
                    });
                    res["success"] = true;
                    res["deletedCount"] = deleted.Count;
                    if (deleted.Count > valid.Count)
                        res["note"] = "지시한 " + valid.Count + "개보다 많은 " + deleted.Count +
                                      "개가 지워졌습니다. 딸린 요소가 함께 삭제된 것입니다.";
                    break;

                default:
                    throw new ArgumentException("모르는 action 입니다: " + action);
            }
            return res;
        }

        static object ModifyElement(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            JArray items = A.Arr(args, "elements");
            if (items == null || items.Count == 0)
                throw new ArgumentException("고칠 요소 목록(elements)이 비어 있습니다.");

            return Rx.Tx<object>(doc, "요소 수정", delegate
            {
                JArray done = new JArray();
                JArray failed = new JArray();

                foreach (JToken t in items)
                {
                    JObject o = t as JObject;
                    if (o == null) continue;
                    int eid = -1;
                    try
                    {
                        eid = A.IntReq(o, "elementId");
                        Element e = doc.GetElement(new ElementId(eid));
                        if (e == null) throw new ArgumentException("요소를 찾을 수 없습니다.");

                        JArray changes = new JArray();

                        // 유형 교체
                        int newTypeId = A.Int(o, "newTypeId", -1);
                        if (newTypeId > 0)
                        {
                            e.ChangeTypeId(new ElementId(newTypeId));
                            changes.Add("유형 교체 -> " + newTypeId);
                        }

                        // 파라미터
                        JObject ps = A.Obj(o, "parameters");
                        if (ps != null)
                        {
                            foreach (JProperty prop in ps.Properties())
                            {
                                string applied = SetParameter(e, prop.Name, prop.Value);
                                changes.Add(applied);
                            }
                        }

                        // 이동
                        XYZ mv = A.PointFt(o["move"]);
                        if (mv != null && mv.GetLength() > 1e-9)
                        {
                            ElementTransformUtils.MoveElement(doc, e.Id, mv);
                            changes.Add("이동");
                        }

                        // 회전
                        if (A.Has(o, "rotate"))
                        {
                            double deg = A.Num(o, "rotate", 0);
                            if (Math.Abs(deg) > 1e-9)
                            {
                                XYZ origin = null;
                                LocationPoint lp = e.Location as LocationPoint;
                                if (lp != null) origin = lp.Point;
                                if (origin == null)
                                {
                                    BoundingBoxXYZ bb = e.get_BoundingBox(null);
                                    if (bb != null) origin = (bb.Min + bb.Max) / 2.0;
                                }
                                if (origin == null) throw new InvalidOperationException("회전 중심을 정할 수 없습니다.");
                                Line axis = Line.CreateBound(origin, origin + XYZ.BasisZ);
                                ElementTransformUtils.RotateElement(doc, e.Id, axis, deg * Math.PI / 180.0);
                                changes.Add("회전 " + deg + "도");
                            }
                        }

                        JObject r = new JObject();
                        r["id"] = eid;
                        r["changes"] = changes;
                        done.Add(r);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new JObject(new JProperty("id", eid), new JProperty("error", ex.Message)));
                    }
                }

                JObject res = new JObject();
                res["success"] = failed.Count == 0;
                res["modifiedCount"] = done.Count;
                res["modified"] = done;
                if (failed.Count > 0) res["failed"] = failed;
                return res;
            });
        }

        static string SetParameter(Element e, string name, JToken value)
        {
            Parameter p = e.LookupParameter(name);
            if (p == null)
            {
                // 내장 파라미터 이름으로도 시도한다.
                BuiltInParameter bip;
                if (System.Enum.TryParse<BuiltInParameter>(name, true, out bip))
                    p = e.get_Parameter(bip);
            }
            if (p == null) throw new ArgumentException("파라미터를 찾을 수 없습니다: " + name);
            if (p.IsReadOnly) throw new InvalidOperationException("읽기 전용 파라미터입니다: " + name);

            switch (p.StorageType)
            {
                case StorageType.Double:
                    {
                        double v = value.Value<double>();
                        ForgeTypeId spec = null;
                        try { spec = p.Definition.GetDataType(); } catch { }
                        // 길이 계열이면 mm 로 들어온 것으로 보고 ft 로 바꾼다.
                        if (spec != null && spec.Equals(SpecTypeId.Length)) v = U.ToFt(v);
                        p.Set(v);
                        return name + " = " + value;
                    }
                case StorageType.Integer:
                    {
                        if (value.Type == JTokenType.Boolean) p.Set(value.Value<bool>() ? 1 : 0);
                        else p.Set(value.Value<int>());
                        return name + " = " + value;
                    }
                case StorageType.String:
                    p.Set(value.Type == JTokenType.Null ? "" : value.ToString());
                    return name + " = " + value;
                case StorageType.ElementId:
                    p.Set(new ElementId(value.Value<int>()));
                    return name + " = " + value;
                default:
                    throw new InvalidOperationException("다룰 수 없는 파라미터 형식입니다: " + name);
            }
        }

        static object DeleteElement(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            List<ElementId> ids = A.ElementIds(args, "elementIds");
            if (ids.Count == 0) throw new ArgumentException("elementIds 가 비어 있습니다.");

            List<ElementId> valid = new List<ElementId>();
            List<int> missing = new List<int>();
            foreach (ElementId id in ids)
            {
                if (doc.GetElement(id) != null) valid.Add(id);
                else missing.Add(id.IntegerValue);
            }
            if (valid.Count == 0)
                throw new ArgumentException("지울 수 있는 요소가 없습니다. 모두 이미 없거나 잘못된 ID 입니다.");

            return Rx.Tx<object>(doc, "요소 삭제", delegate
            {
                ICollection<ElementId> deleted = doc.Delete(valid);

                JObject res = new JObject();
                res["success"] = true;
                res["requested"] = ids.Count;
                res["deletedCount"] = deleted.Count;
                if (missing.Count > 0) res["notFound"] = new JArray(missing.ToArray());
                if (deleted.Count > valid.Count)
                    res["note"] = "지시한 " + valid.Count + "개보다 많은 " + deleted.Count +
                                  "개가 지워졌습니다. 딸린 요소가 함께 삭제된 것입니다.";
                return res;
            });
        }

        static object ColorElements(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            View view = doc.ActiveView;
            if (view == null) throw new InvalidOperationException("활성 뷰가 없습니다.");

            string catName = A.StrReq(args, "categoryName");
            string paramName = A.StrReq(args, "parameterName");
            bool gradient = A.Bool(args, "useGradient", false);
            JArray custom = A.Arr(args, "customColors");

            BuiltInCategory bic = Rx.ResolveCategory(doc, catName);
            if (bic == BuiltInCategory.INVALID)
                throw new ArgumentException("알 수 없는 카테고리: " + catName);

            // 값별로 요소를 모은다.
            Dictionary<string, List<ElementId>> groups = new Dictionary<string, List<ElementId>>();
            foreach (Element e in new FilteredElementCollector(doc, view.Id)
                        .OfCategory(bic).WhereElementIsNotElementType())
            {
                Parameter p = e.LookupParameter(paramName);
                if (p == null)
                {
                    BuiltInParameter bip;
                    if (System.Enum.TryParse<BuiltInParameter>(paramName, true, out bip))
                        p = e.get_Parameter(bip);
                }
                string key = "(값 없음)";
                if (p != null && p.HasValue)
                {
                    JToken v = Rx.ParamValue(p);
                    key = v == null || v.Type == JTokenType.Null ? "(값 없음)" : v.ToString();
                }

                List<ElementId> list;
                if (!groups.TryGetValue(key, out list)) { list = new List<ElementId>(); groups[key] = list; }
                list.Add(e.Id);
            }

            if (groups.Count == 0)
                throw new InvalidOperationException(
                    "현재 뷰에서 '" + catName + "' 카테고리 요소를 찾지 못했습니다.");

            ElementId solidId = FindSolidFillPattern(doc);

            return Rx.Tx<object>(doc, "요소 색분류", delegate
            {
                JArray results = new JArray();
                int total = 0;
                int index = 0;

                foreach (KeyValuePair<string, List<ElementId>> kv in groups)
                {
                    Color color = PickColor(index, groups.Count, gradient, custom);

                    OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetSurfaceForegroundPatternVisible(true);
                    ogs.SetProjectionLineColor(color);
                    ogs.SetCutForegroundPatternColor(color);
                    ogs.SetCutForegroundPatternVisible(true);
                    if (solidId != ElementId.InvalidElementId)
                    {
                        ogs.SetSurfaceForegroundPatternId(solidId);
                        ogs.SetCutForegroundPatternId(solidId);
                    }

                    foreach (ElementId id in kv.Value)
                    {
                        try { view.SetElementOverrides(id, ogs); total++; }
                        catch { }
                    }

                    JObject g = new JObject();
                    g["parameterValue"] = kv.Key;
                    g["count"] = kv.Value.Count;
                    g["color"] = new JObject(
                        new JProperty("r", (int)color.Red),
                        new JProperty("g", (int)color.Green),
                        new JProperty("b", (int)color.Blue));
                    results.Add(g);
                    index++;
                }

                JObject res = new JObject();
                res["success"] = true;
                res["view"] = view.Name;
                res["totalElements"] = total;
                res["coloredGroups"] = groups.Count;
                res["results"] = results;
                return res;
            });
        }

        static ElementId FindSolidFillPattern(Document doc)
        {
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)))
            {
                FillPatternElement fpe = e as FillPatternElement;
                if (fpe == null) continue;
                try
                {
                    FillPattern fp = fpe.GetFillPattern();
                    if (fp != null && fp.IsSolidFill) return fpe.Id;
                }
                catch { }
            }
            return ElementId.InvalidElementId;
        }

        static Color PickColor(int index, int count, bool gradient, JArray custom)
        {
            if (custom != null && index < custom.Count)
            {
                JObject c = custom[index] as JObject;
                if (c != null)
                {
                    return new Color(
                        (byte)Math.Max(0, Math.Min(255, A.Int(c, "r", 0))),
                        (byte)Math.Max(0, Math.Min(255, A.Int(c, "g", 0))),
                        (byte)Math.Max(0, Math.Min(255, A.Int(c, "b", 0))));
                }
            }

            // 색상환을 균등하게 돌면서 뽑는다. gradient 면 순서대로, 아니면 황금각으로 흩는다.
            double hue = gradient
                ? (count <= 1 ? 0.0 : 360.0 * index / count)
                : (index * 137.50776405) % 360.0;
            return FromHsv(hue, 0.65, 0.95);
        }

        static Color FromHsv(double h, double s, double v)
        {
            int hi = (int)Math.Floor(h / 60.0) % 6;
            double f = h / 60.0 - Math.Floor(h / 60.0);
            double p = v * (1 - s);
            double q = v * (1 - f * s);
            double t = v * (1 - (1 - f) * s);
            double r, g, b;
            switch (hi)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return new Color((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }
}
