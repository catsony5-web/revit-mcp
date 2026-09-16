using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    internal static class QueryTools
    {
        public static void Register()
        {
            ToolRegistry.Register("ai_element_filter",
                "여러 조건을 조합해 모델에서 요소를 찾는다. 카테고리, 클래스 이름, 패밀리 유형 ID, 현재 뷰 가시성, " +
                "공간 경계상자로 거를 수 있다. 좌표는 mm. 인스턴스와 유형 중 무엇을 포함할지 고를 수 있다.",
                S.Obj()
                    .Str("filterCategory", "카테고리. 'OST_Walls' 같은 내장 이름이나 'Walls'/'벽' 같은 표시 이름.")
                    .Str("filterElementType", "요소 클래스 이름. 예: 'Wall', 'Floor', 'FamilyInstance'.")
                    .Int("filterFamilySymbolId", "특정 패밀리 유형(FamilySymbol)의 ElementId. 쓰지 않으면 생략하거나 -1.")
                    .Bool("includeTypes", "유형 요소(벽 유형, 문 유형 등)를 포함할지", false)
                    .Bool("includeInstances", "배치된 인스턴스를 포함할지", true)
                    .Bool("filterVisibleInCurrentView", "현재 뷰에 보이는 것만 돌려줄지. 인스턴스에만 적용된다.", false)
                    .Pt("boundingBoxMin", "공간 필터 최소점 (mm)", false)
                    .Pt("boundingBoxMax", "공간 필터 최대점 (mm)", false)
                    .Bool("includeParameters", "요소별 파라미터 전체를 함께 줄지. 응답이 매우 커진다.", false)
                    .IntDef("maxElements", "돌려줄 최대 요소 수", 50),
                AiElementFilter);

            ToolRegistry.Register("get_selected_elements",
                "지금 Revit 화면에서 사용자가 선택해 둔 요소들을 돌려준다. 사용자가 '이거', '선택한 것' 이라고 " +
                "지칭할 때 쓴다.",
                S.Obj()
                    .Bool("includeParameters", "요소별 파라미터 전체를 함께 줄지", false)
                    .IntDef("limit", "돌려줄 최대 요소 수", 200),
                GetSelectedElements);

            ToolRegistry.Register("get_available_family_types",
                "프로젝트에 로드된 패밀리 유형(FamilySymbol 및 기타 ElementType)을 돌려준다. 요소를 배치하기 전에 " +
                "쓸 수 있는 유형 ID를 알아내는 용도다.",
                S.Obj()
                    .Str("categoryName", "카테고리로 좁힌다. 예: 'OST_Doors', 'Windows', '문'.")
                    .Str("familyNameFilter", "패밀리 이름에 이 문자열이 들어간 것만. 대소문자를 가리지 않는다.")
                    .Str("typeNameFilter", "유형 이름에 이 문자열이 들어간 것만. 대소문자를 가리지 않는다.")
                    .Bool("onlyPlaceable", "배치 가능한 FamilySymbol 만 돌려줄지", false)
                    .IntDef("limit", "돌려줄 최대 유형 수", 200),
                GetAvailableFamilyTypes);

            ToolRegistry.Register("get_material_quantities",
                "재료별 물량(부피 m3, 면적 m2)을 집계한다. 카테고리로 범위를 좁힐 수 있다. 물량 산출과 " +
                "재료 검토에 쓴다.",
                S.Obj()
                    .Str("categoryName", "집계 범위를 이 카테고리로 좁힌다. 비우면 모델 전체.")
                    .Bool("byCategory", "재료별 합계를 카테고리별로 쪼개서 볼지", false)
                    .IntArr("elementIds", "이 요소들만 집계한다. 주면 categoryName 보다 우선한다.", false),
                GetMaterialQuantities);

            ToolRegistry.Register("analyze_model_statistics",
                "모델 규모와 구성을 요약한다. 총 요소/유형/패밀리/뷰/시트 수, 카테고리별 개수, 레벨별 요소 분포를 준다. " +
                "모델 감사나 성능 점검의 출발점으로 쓴다.",
                S.Obj()
                    .Bool("includeDetailedTypes", "카테고리별로 패밀리·유형 내역까지 쪼개서 볼지", true)
                    .IntDef("topCategories", "개수 상위 몇 개 카테고리까지 자세히 볼지", 30),
                AnalyzeModelStatistics);
        }

        static object AiElementFilter(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);

            // 원래 스키마는 인자를 data 아래에 감쌌다. 두 형태를 모두 받는다.
            JObject a = A.Obj(args, "data");
            if (a == null) a = args;

            bool includeTypes = A.Bool(a, "includeTypes", false);
            bool includeInstances = A.Bool(a, "includeInstances", true);
            if (!includeTypes && !includeInstances)
                throw new ArgumentException("includeTypes 와 includeInstances 가 모두 false 라 찾을 대상이 없습니다.");

            bool visibleOnly = A.Bool(a, "filterVisibleInCurrentView", false);
            int max = A.Int(a, "maxElements", 50);
            if (max <= 0) max = 50;
            bool withParams = A.Bool(a, "includeParameters", false);

            List<Element> found = new List<Element>();
            int totalMatched = 0;

            // 인스턴스와 유형은 수집기를 나눠서 돌린다.
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantTypes = (pass == 1);
                if (wantTypes && !includeTypes) continue;
                if (!wantTypes && !includeInstances) continue;

                FilteredElementCollector col = (visibleOnly && !wantTypes && doc.ActiveView != null)
                    ? new FilteredElementCollector(doc, doc.ActiveView.Id)
                    : new FilteredElementCollector(doc);

                col = wantTypes ? col.WhereElementIsElementType() : col.WhereElementIsNotElementType();

                string catName = A.Str(a, "filterCategory", null);
                if (!string.IsNullOrEmpty(catName))
                {
                    BuiltInCategory bic = Rx.ResolveCategory(doc, catName);
                    if (bic == BuiltInCategory.INVALID)
                        throw new ArgumentException("알 수 없는 카테고리: " + catName);
                    col = col.OfCategory(bic);
                }

                XYZ bbMin = A.PointFt(a["boundingBoxMin"]);
                XYZ bbMax = A.PointFt(a["boundingBoxMax"]);
                if (bbMin != null && bbMax != null && !wantTypes)
                {
                    Outline outline = new Outline(
                        new XYZ(Math.Min(bbMin.X, bbMax.X), Math.Min(bbMin.Y, bbMax.Y), Math.Min(bbMin.Z, bbMax.Z)),
                        new XYZ(Math.Max(bbMin.X, bbMax.X), Math.Max(bbMin.Y, bbMax.Y), Math.Max(bbMin.Z, bbMax.Z)));
                    col = col.WherePasses(new BoundingBoxIntersectsFilter(outline));
                }

                string className = A.Str(a, "filterElementType", null);
                int symbolId = A.Int(a, "filterFamilySymbolId", -1);

                foreach (Element e in col)
                {
                    if (!string.IsNullOrEmpty(className) && !MatchesClass(e, className)) continue;
                    if (symbolId > 0)
                    {
                        ElementId tid = e.GetTypeId();
                        bool isThatType = (tid != null && tid.IntegerValue == symbolId) || e.Id.IntegerValue == symbolId;
                        if (!isThatType) continue;
                    }

                    totalMatched++;
                    if (found.Count < max) found.Add(e);
                }
            }

            JArray arr = new JArray();
            foreach (Element e in found) arr.Add(Rx.Describe(doc, e, withParams));

            JObject res = new JObject();
            res["totalMatched"] = totalMatched;
            res["returned"] = found.Count;
            if (totalMatched > found.Count)
                res["note"] = "조건에 맞는 것은 " + totalMatched + "개인데 " + found.Count +
                              "개만 돌려주었습니다. maxElements 를 올리거나 조건을 좁히십시오.";
            res["elements"] = arr;
            return res;
        }

        static bool MatchesClass(Element e, string className)
        {
            Type t = e.GetType();
            while (t != null)
            {
                if (string.Equals(t.Name, className, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(t.FullName, className, StringComparison.OrdinalIgnoreCase)) return true;
                t = t.BaseType;
            }
            return false;
        }

        static object GetSelectedElements(UIApplication uiapp, JObject args)
        {
            UIDocument uidoc = Rx.UiDoc(uiapp);
            Document doc = uidoc.Document;

            bool withParams = A.Bool(args, "includeParameters", false);
            int limit = A.Int(args, "limit", 200);

            ICollection<ElementId> ids = uidoc.Selection.GetElementIds();
            JArray arr = new JArray();
            int i = 0;
            foreach (ElementId id in ids)
            {
                if (i++ >= limit) break;
                Element e = doc.GetElement(id);
                if (e != null) arr.Add(Rx.Describe(doc, e, withParams));
            }

            JObject res = new JObject();
            res["selectedCount"] = ids.Count;
            res["returned"] = arr.Count;
            if (ids.Count == 0) res["message"] = "선택된 요소가 없습니다. Revit 화면에서 요소를 먼저 선택하십시오.";
            res["elements"] = arr;
            return res;
        }

        static object GetAvailableFamilyTypes(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);

            string catName = A.Str(args, "categoryName", null);
            string famFilter = A.Str(args, "familyNameFilter", null);
            string typeFilter = A.Str(args, "typeNameFilter", null);
            bool onlyPlaceable = A.Bool(args, "onlyPlaceable", false);
            int limit = A.Int(args, "limit", 200);

            FilteredElementCollector col = new FilteredElementCollector(doc).WhereElementIsElementType();
            if (!string.IsNullOrEmpty(catName))
            {
                BuiltInCategory bic = Rx.ResolveCategory(doc, catName);
                if (bic == BuiltInCategory.INVALID)
                    throw new ArgumentException("알 수 없는 카테고리: " + catName);
                col = col.OfCategory(bic);
            }

            JArray arr = new JArray();
            int total = 0;
            foreach (Element e in col)
            {
                ElementType et = e as ElementType;
                if (et == null) continue;

                FamilySymbol fs = et as FamilySymbol;
                if (onlyPlaceable && fs == null) continue;

                string fam = et.FamilyName;
                string typeName = et.Name;
                if (!string.IsNullOrEmpty(famFilter) &&
                    (fam == null || fam.IndexOf(famFilter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (!string.IsNullOrEmpty(typeFilter) &&
                    (typeName == null || typeName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0)) continue;

                total++;
                if (arr.Count >= limit) continue;

                JObject o = new JObject();
                o["typeId"] = et.Id.IntegerValue;
                o["familyName"] = fam;
                o["typeName"] = typeName;
                o["category"] = et.Category != null ? et.Category.Name : null;
                o["class"] = et.GetType().Name;
                if (fs != null) o["isActive"] = fs.IsActive;
                arr.Add(o);
            }

            JObject res = new JObject();
            res["totalMatched"] = total;
            res["returned"] = arr.Count;
            if (total > arr.Count)
                res["note"] = "총 " + total + "개 중 " + arr.Count + "개만 돌려주었습니다. 필터로 좁히십시오.";
            res["types"] = arr;
            return res;
        }

        static object GetMaterialQuantities(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);

            List<Element> targets = new List<Element>();
            List<ElementId> explicitIds = A.ElementIds(args, "elementIds");
            if (explicitIds.Count > 0)
            {
                foreach (ElementId id in explicitIds)
                {
                    Element e = doc.GetElement(id);
                    if (e != null) targets.Add(e);
                }
            }
            else
            {
                FilteredElementCollector col = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                string catName = A.Str(args, "categoryName", null);
                if (!string.IsNullOrEmpty(catName))
                {
                    BuiltInCategory bic = Rx.ResolveCategory(doc, catName);
                    if (bic == BuiltInCategory.INVALID)
                        throw new ArgumentException("알 수 없는 카테고리: " + catName);
                    col = col.OfCategory(bic);
                }
                targets.AddRange(col.ToElements());
            }

            bool byCategory = A.Bool(args, "byCategory", false);

            // key -> [volume ft3, area ft2, count]
            Dictionary<string, double[]> acc = new Dictionary<string, double[]>();

            foreach (Element e in targets)
            {
                ICollection<ElementId> matIds;
                try { matIds = e.GetMaterialIds(false); }
                catch { continue; }
                if (matIds == null) continue;

                foreach (ElementId mid in matIds)
                {
                    Material m = doc.GetElement(mid) as Material;
                    if (m == null) continue;

                    double vol = 0, area = 0;
                    try { vol = e.GetMaterialVolume(mid); } catch { }
                    try { area = e.GetMaterialArea(mid, false); } catch { }

                    string key = byCategory && e.Category != null ? e.Category.Name + " / " + m.Name : m.Name;
                    double[] slot;
                    if (!acc.TryGetValue(key, out slot)) { slot = new double[3]; acc[key] = slot; }
                    slot[0] += vol;
                    slot[1] += area;
                    slot[2] += 1;
                }
            }

            // ft3 -> m3, ft2 -> m2
            const double Ft3ToM3 = 0.0283168466;
            const double Ft2ToM2 = 0.09290304;

            JArray arr = new JArray();
            double totalVol = 0;
            foreach (KeyValuePair<string, double[]> kv in acc)
            {
                JObject o = new JObject();
                o["material"] = kv.Key;
                o["volume_m3"] = Math.Round(kv.Value[0] * Ft3ToM3, 4);
                o["area_m2"] = Math.Round(kv.Value[1] * Ft2ToM2, 4);
                o["elementCount"] = (int)kv.Value[2];
                arr.Add(o);
                totalVol += kv.Value[0] * Ft3ToM3;
            }

            JObject res = new JObject();
            res["elementsScanned"] = targets.Count;
            res["materialCount"] = acc.Count;
            res["totalVolume_m3"] = Math.Round(totalVol, 4);
            res["materials"] = arr;
            if (acc.Count == 0)
                res["message"] = "재료 물량이 잡히지 않았습니다. 대상 요소에 재료가 지정되어 있는지 확인하십시오.";
            return res;
        }

        static object AnalyzeModelStatistics(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            bool detailed = A.Bool(args, "includeDetailedTypes", true);
            int topN = A.Int(args, "topCategories", 30);

            int totalInstances = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
            int totalTypes = new FilteredElementCollector(doc).WhereElementIsElementType().GetElementCount();
            int totalFamilies = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount();
            int totalViews = new FilteredElementCollector(doc).OfClass(typeof(View)).GetElementCount();
            int totalSheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).GetElementCount();

            Dictionary<string, int> byCat = new Dictionary<string, int>();
            Dictionary<string, int> byLevel = new Dictionary<string, int>();
            Dictionary<string, Dictionary<string, int>> catDetail = new Dictionary<string, Dictionary<string, int>>();

            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                string cat = e.Category != null ? e.Category.Name : "(카테고리 없음)";
                int c;
                byCat[cat] = byCat.TryGetValue(cat, out c) ? c + 1 : 1;

                if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId)
                {
                    Element lv = doc.GetElement(e.LevelId);
                    string ln = lv != null ? lv.Name : "(레벨 없음)";
                    byLevel[ln] = byLevel.TryGetValue(ln, out c) ? c + 1 : 1;
                }

                if (detailed)
                {
                    ElementId tid = e.GetTypeId();
                    if (tid != null && tid != ElementId.InvalidElementId)
                    {
                        ElementType et = doc.GetElement(tid) as ElementType;
                        if (et != null)
                        {
                            Dictionary<string, int> inner;
                            if (!catDetail.TryGetValue(cat, out inner))
                            {
                                inner = new Dictionary<string, int>();
                                catDetail[cat] = inner;
                            }
                            string key = (string.IsNullOrEmpty(et.FamilyName) ? "(패밀리 없음)" : et.FamilyName) + " : " + et.Name;
                            inner[key] = inner.TryGetValue(key, out c) ? c + 1 : 1;
                        }
                    }
                }
            }

            List<KeyValuePair<string, int>> catList = new List<KeyValuePair<string, int>>(byCat);
            catList.Sort(delegate(KeyValuePair<string, int> x, KeyValuePair<string, int> y) { return y.Value.CompareTo(x.Value); });

            JArray cats = new JArray();
            int shown = 0;
            foreach (KeyValuePair<string, int> kv in catList)
            {
                JObject o = new JObject();
                o["category"] = kv.Key;
                o["count"] = kv.Value;
                if (detailed && shown < topN)
                {
                    Dictionary<string, int> inner;
                    if (catDetail.TryGetValue(kv.Key, out inner))
                    {
                        JObject t = new JObject();
                        foreach (KeyValuePair<string, int> iv in inner) t[iv.Key] = iv.Value;
                        o["types"] = t;
                    }
                }
                cats.Add(o);
                shown++;
            }

            JObject levels = new JObject();
            foreach (KeyValuePair<string, int> kv in byLevel) levels[kv.Key] = kv.Value;

            JObject res = new JObject();
            res["documentTitle"] = doc.Title;
            res["totalElements"] = totalInstances;
            res["totalTypes"] = totalTypes;
            res["totalFamilies"] = totalFamilies;
            res["totalViews"] = totalViews;
            res["totalSheets"] = totalSheets;
            res["categoryCount"] = byCat.Count;
            res["byCategory"] = cats;
            res["byLevel"] = levels;
            return res;
        }
    }
}
