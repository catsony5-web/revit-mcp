using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    internal static class ViewTools
    {
        public static void Register()
        {
            ToolRegistry.Register("get_current_view_info",
                "현재 활성 뷰의 정보를 돌려준다. 뷰 이름, 종류, 축척, 상세 수준, 연관 레벨, 자르기 영역 범위(mm), " +
                "단면 상자 활성 여부를 포함한다.",
                S.Obj(),
                GetCurrentViewInfo);

            ToolRegistry.Register("get_current_view_elements",
                "현재 활성 뷰에 보이는 요소들을 돌려준다. 카테고리로 걸러낼 수 있고 요소별로 ID, 이름, 유형, 레벨, " +
                "위치, 경계상자(mm)를 준다. 기본 상한은 100개이며 그보다 많으면 잘라내고 총 개수를 알려준다.",
                S.Obj()
                    .StrArr("categories", "걸러낼 카테고리. 'OST_Walls' 같은 내장 이름이나 'Walls'/'벽' 같은 표시 이름 모두 가능. 비우면 전체.", false)
                    .Bool("includeParameters", "요소별 파라미터 전체를 함께 줄지 여부. 응답이 매우 커지므로 필요할 때만 켠다.", false)
                    .IntDef("limit", "돌려줄 최대 요소 수", 100),
                GetCurrentViewElements);
        }

        static object GetCurrentViewInfo(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            View v = doc.ActiveView;
            if (v == null) throw new InvalidOperationException("활성 뷰가 없습니다.");

            JObject o = new JObject();
            o["id"] = v.Id.IntegerValue;
            o["name"] = v.Name;
            o["viewType"] = v.ViewType.ToString();
            o["isTemplate"] = v.IsTemplate;
            o["scale"] = v.Scale;
            o["detailLevel"] = v.DetailLevel.ToString();
            o["displayStyle"] = v.DisplayStyle.ToString();
            o["discipline"] = v.Discipline.ToString();
            o["documentTitle"] = doc.Title;
            o["documentPath"] = doc.PathName;

            try
            {
                if (v.GenLevel != null)
                {
                    o["level"] = v.GenLevel.Name;
                    o["levelElevation"] = Math.Round(U.ToMm(v.GenLevel.Elevation), 2);
                }
            }
            catch { }

            try
            {
                o["cropBoxActive"] = v.CropBoxActive;
                if (v.CropBoxActive && v.CropBox != null)
                {
                    o["cropBox"] = new JObject(
                        new JProperty("min", Rx.PtJson(v.CropBox.Min)),
                        new JProperty("max", Rx.PtJson(v.CropBox.Max)));
                }
            }
            catch { }

            View3D v3 = v as View3D;
            if (v3 != null)
            {
                o["isPerspective"] = v3.IsPerspective;
                try
                {
                    o["sectionBoxActive"] = v3.IsSectionBoxActive;
                    if (v3.IsSectionBoxActive)
                    {
                        BoundingBoxXYZ sb = v3.GetSectionBox();
                        if (sb != null)
                        {
                            o["sectionBox"] = new JObject(
                                new JProperty("min", Rx.PtJson(sb.Min)),
                                new JProperty("max", Rx.PtJson(sb.Max)));
                        }
                    }
                }
                catch { }
            }

            try
            {
                ViewSheet sheet = v as ViewSheet;
                if (sheet != null)
                {
                    o["sheetNumber"] = sheet.SheetNumber;
                    o["sheetName"] = sheet.Name;
                }
            }
            catch { }

            return o;
        }

        static object GetCurrentViewElements(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            View v = doc.ActiveView;
            if (v == null) throw new InvalidOperationException("활성 뷰가 없습니다.");

            bool withParams = A.Bool(args, "includeParameters", false);
            int limit = A.Int(args, "limit", 100);
            if (limit <= 0) limit = 100;

            FilteredElementCollector col = new FilteredElementCollector(doc, v.Id)
                .WhereElementIsNotElementType();

            JArray cats = A.Arr(args, "categories");
            if (cats != null && cats.Count > 0)
            {
                List<ElementFilter> filters = new List<ElementFilter>();
                List<string> unresolved = new List<string>();
                foreach (JToken t in cats)
                {
                    BuiltInCategory bic = Rx.ResolveCategory(doc, t.ToString());
                    if (bic == BuiltInCategory.INVALID) { unresolved.Add(t.ToString()); continue; }
                    filters.Add(new ElementCategoryFilter(bic));
                }
                if (unresolved.Count > 0)
                    throw new ArgumentException("알 수 없는 카테고리: " + string.Join(", ", unresolved.ToArray()));
                if (filters.Count == 1) col = col.WherePasses(filters[0]);
                else if (filters.Count > 1) col = col.WherePasses(new LogicalOrFilter(filters));
            }

            List<Element> all = new List<Element>(col.ToElements());

            JArray arr = new JArray();
            int n = Math.Min(limit, all.Count);
            for (int i = 0; i < n; i++)
                arr.Add(Rx.Describe(doc, all[i], withParams));

            JObject res = new JObject();
            res["viewName"] = v.Name;
            res["totalFound"] = all.Count;
            res["returned"] = n;
            if (all.Count > n)
                res["note"] = "총 " + all.Count + "개 중 " + n + "개만 돌려주었습니다. limit 를 올리거나 categories 로 좁히십시오.";
            res["elements"] = arr;
            return res;
        }
    }
}
