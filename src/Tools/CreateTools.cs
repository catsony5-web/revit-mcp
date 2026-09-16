using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    internal static class CreateTools
    {
        public static void Register()
        {
            ToolRegistry.Register("create_level",
                "지정한 표고에 레벨을 하나 이상 만든다. 표고 단위는 mm 이며 프로젝트 원점 기준이다.",
                S.Obj()
                    .ObjArr("data", "만들 레벨 목록",
                        S.Obj()
                            .StrReq("name", "레벨 이름. 예: '3F', '지붕층'")
                            .NumReq("elevation", "프로젝트 원점 기준 표고 (mm)")
                            .Str("description", "설명 (선택)"),
                        true),
                CreateLevel);

            ToolRegistry.Register("create_grid",
                "X 방향과 Y 방향 통심(그리드)을 일정 간격으로 만든다. 이름 규칙은 알파벳(A,B,C) 또는 숫자(1,2,3) 중 " +
                "고른다. 모든 길이 단위는 mm.",
                S.Obj()
                    .IntDef("xCount", "X 방향(세로선) 통심 개수", 0)
                    .NumDef("xSpacing", "X 방향 통심 간격 (mm)", 0)
                    .StrDef("xStartLabel", "X 방향 시작 이름", "A")
                    .Enum("xNamingStyle", "X 방향 이름 규칙", new string[] { "alphabetic", "numeric" }, "alphabetic")
                    .NumDef("xStartPosition", "첫 X 통심의 X 좌표 (mm)", 0)
                    .IntDef("yCount", "Y 방향(가로선) 통심 개수", 0)
                    .NumDef("ySpacing", "Y 방향 통심 간격 (mm)", 0)
                    .StrDef("yStartLabel", "Y 방향 시작 이름", "1")
                    .Enum("yNamingStyle", "Y 방향 이름 규칙", new string[] { "alphabetic", "numeric" }, "numeric")
                    .NumDef("yStartPosition", "첫 Y 통심의 Y 좌표 (mm)", 0)
                    .NumDef("xExtentMin", "Y 방향 통심이 시작하는 X 좌표 (mm)", 0)
                    .NumDef("xExtentMax", "Y 방향 통심이 끝나는 X 좌표 (mm)", 50000)
                    .NumDef("yExtentMin", "X 방향 통심이 시작하는 Y 좌표 (mm)", 0)
                    .NumDef("yExtentMax", "X 방향 통심이 끝나는 Y 좌표 (mm)", 50000)
                    .NumDef("elevation", "통심을 놓을 표고 (mm)", 0),
                CreateGrid);

            ToolRegistry.Register("create_room",
                "방(Room)을 만든다. point 를 주면 그 자리에 하나씩 놓고, autoPlace 를 켜면 해당 레벨에서 벽으로 " +
                "둘러싸인 모든 영역에 자동으로 배치한다.",
                S.Obj()
                    .Str("levelName", "대상 레벨 이름. 비우면 활성 뷰의 레벨을 쓴다.")
                    .Int("levelId", "대상 레벨의 ElementId. levelName 보다 우선한다.")
                    .Bool("autoPlace", "벽으로 둘러싸인 모든 영역에 자동 배치할지", false)
                    .ObjArr("rooms", "개별 배치할 방 목록",
                        S.Obj()
                            .Pt("point", "방을 놓을 위치 (mm). Z 는 무시된다.", true)
                            .Str("name", "방 이름 (선택)")
                            .Str("number", "방 번호 (선택)"),
                        false),
                CreateRoom);

            ToolRegistry.Register("create_point_based_element",
                "점 위치에 패밀리 인스턴스를 배치한다. 가구, 기기, 위생기구, 조명처럼 한 점으로 놓이는 요소에 쓴다. " +
                "배치할 유형 ID는 get_available_family_types 로 먼저 확인한다.",
                S.Obj()
                    .ObjArr("elements", "배치할 요소 목록",
                        S.Obj()
                            .IntReq("typeId", "배치할 FamilySymbol 의 ElementId")
                            .Pt("point", "배치 위치 (mm)", true)
                            .Str("levelName", "기준 레벨 이름 (선택). 비우면 활성 뷰 레벨.")
                            .Int("levelId", "기준 레벨의 ElementId (선택)")
                            .NumDef("rotation", "Z축 기준 회전 각도 (도)", 0)
                            .Int("hostId", "호스트 요소의 ElementId. 벽에 붙는 문·창이면 반드시 준다."),
                        true),
                CreatePointBased);

            ToolRegistry.Register("create_line_based_element",
                "두 점을 잇는 선형 요소를 만든다. 벽이면 typeId 에 벽 유형을, 보·배관 등이면 해당 패밀리 유형을 준다. " +
                "모든 좌표는 mm.",
                S.Obj()
                    .ObjArr("elements", "만들 요소 목록",
                        S.Obj()
                            .IntReq("typeId", "벽 유형 또는 선형 패밀리 유형의 ElementId")
                            .Pt("start", "시작점 (mm)", true)
                            .Pt("end", "끝점 (mm)", true)
                            .Str("levelName", "기준 레벨 이름 (선택)")
                            .Int("levelId", "기준 레벨의 ElementId (선택)")
                            .NumDef("height", "벽 높이 (mm). 벽일 때만 쓰인다.", 3000)
                            .NumDef("offset", "레벨로부터의 오프셋 (mm)", 0)
                            .Bool("structural", "구조 요소로 만들지", false),
                        true),
                CreateLineBased);

            ToolRegistry.Register("create_surface_based_element",
                "경계점들을 이어 면 요소(바닥, 천장, 지붕)를 만든다. 경계는 닫힌 다각형이어야 하며 좌표는 mm.",
                S.Obj()
                    .Enum("elementType", "만들 요소 종류", new string[] { "Floor", "Ceiling", "Roof" }, "Floor")
                    .IntReq("typeId", "바닥·천장·지붕 유형의 ElementId")
                    .Str("levelName", "기준 레벨 이름 (선택)")
                    .Int("levelId", "기준 레벨의 ElementId (선택)")
                    .NumDef("offset", "레벨로부터의 높이 오프셋 (mm)", 0)
                    .ObjArr("boundary", "경계 다각형의 꼭짓점 목록. 순서대로 이어지며 마지막 점은 첫 점과 자동으로 닫힌다.",
                        S.Point("꼭짓점"), true),
                CreateSurfaceBased);

            ToolRegistry.Register("create_structural_framing_system",
                "직사각형 영역에 구조 보를 일정 간격으로 깔아 보 시스템을 만든다. 경계와 간격, 보 유형을 준다.",
                S.Obj()
                    .IntReq("beamTypeId", "보 패밀리 유형의 ElementId")
                    .Str("levelName", "기준 레벨 이름 (선택)")
                    .Int("levelId", "기준 레벨의 ElementId (선택)")
                    .NumReq("spacing", "보 사이 간격 (mm)")
                    .Enum("direction", "보가 뻗는 방향", new string[] { "x", "y" }, "x")
                    .NumDef("offset", "레벨로부터의 오프셋 (mm)", 0)
                    .ObjArr("boundary", "보를 깔 직사각형 영역의 꼭짓점 목록 (mm)", S.Point("꼭짓점"), true),
                CreateFramingSystem);
        }

        // ---- 공통 도우미 ----

        internal static Level ResolveLevel(Document doc, JObject o, bool required)
        {
            int lid = A.Int(o, "levelId", -1);
            if (lid > 0)
            {
                Level l = doc.GetElement(new ElementId(lid)) as Level;
                if (l != null) return l;
                throw new ArgumentException("levelId " + lid + " 는 레벨이 아닙니다.");
            }

            string name = A.Str(o, "levelName", null);
            if (!string.IsNullOrEmpty(name))
            {
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Level)))
                {
                    if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return (Level)e;
                }
                throw new ArgumentException("그런 이름의 레벨이 없습니다: " + name);
            }

            // 활성 뷰의 레벨
            View v = doc.ActiveView;
            if (v != null && v.GenLevel != null) return v.GenLevel;

            if (!required) return null;

            // 마지막 수단: 가장 낮은 레벨
            Level lowest = null;
            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Level)))
            {
                Level l = (Level)e;
                if (lowest == null || l.Elevation < lowest.Elevation) lowest = l;
            }
            if (lowest == null) throw new InvalidOperationException("프로젝트에 레벨이 하나도 없습니다.");
            return lowest;
        }

        static FamilySymbol GetSymbol(Document doc, int typeId)
        {
            FamilySymbol fs = doc.GetElement(new ElementId(typeId)) as FamilySymbol;
            if (fs == null)
                throw new ArgumentException("typeId " + typeId + " 는 배치 가능한 패밀리 유형(FamilySymbol)이 아닙니다. " +
                                            "get_available_family_types 로 올바른 ID를 확인하십시오.");
            if (!fs.IsActive) fs.Activate();
            return fs;
        }

        static string LabelAt(string startLabel, int index, string style)
        {
            if (style == "numeric")
            {
                int start;
                if (!int.TryParse(startLabel, out start)) start = 1;
                return (start + index).ToString();
            }

            // 알파벳: A..Z, AA, AB ...
            int baseIdx = 0;
            if (!string.IsNullOrEmpty(startLabel))
            {
                string s = startLabel.ToUpperInvariant();
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c < 'A' || c > 'Z') { baseIdx = 0; break; }
                    baseIdx = baseIdx * 26 + (c - 'A' + 1);
                }
                baseIdx = baseIdx > 0 ? baseIdx - 1 : 0;
            }
            int n = baseIdx + index;
            string result = "";
            n++;
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                result = (char)('A' + rem) + result;
                n = (n - 1) / 26;
            }
            return result;
        }

        // ---- 구현 ----

        static object CreateLevel(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            JArray data = A.Arr(args, "data");
            if (data == null || data.Count == 0)
                throw new ArgumentException("만들 레벨 목록(data)이 비어 있습니다.");

            return Rx.Tx<object>(doc, "레벨 생성", delegate
            {
                JArray created = new JArray();
                JArray failed = new JArray();

                foreach (JToken t in data)
                {
                    JObject o = t as JObject;
                    if (o == null) continue;
                    string name = A.Str(o, "name", null);
                    try
                    {
                        double elevMm = A.NumReq(o, "elevation");
                        Level lv = Level.Create(doc, U.ToFt(elevMm));
                        if (!string.IsNullOrEmpty(name))
                        {
                            try { lv.Name = name; }
                            catch (Exception ex) { Log.Warn("레벨 이름 지정 실패 (" + name + "): " + ex.Message); }
                        }
                        JObject r = new JObject();
                        r["id"] = lv.Id.IntegerValue;
                        r["name"] = lv.Name;
                        r["elevation"] = Math.Round(U.ToMm(lv.Elevation), 2);
                        created.Add(r);
                    }
                    catch (Exception ex)
                    {
                        JObject f = new JObject();
                        f["name"] = name;
                        f["error"] = ex.Message;
                        failed.Add(f);
                    }
                }

                JObject res = new JObject();
                res["success"] = failed.Count == 0;
                res["createdCount"] = created.Count;
                res["created"] = created;
                if (failed.Count > 0) res["failed"] = failed;
                return res;
            });
        }

        static object CreateGrid(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);

            int xCount = A.Int(args, "xCount", 0);
            int yCount = A.Int(args, "yCount", 0);
            if (xCount <= 0 && yCount <= 0)
                throw new ArgumentException("xCount 와 yCount 가 모두 0 이라 만들 통심이 없습니다.");

            double xSpacing = A.Num(args, "xSpacing", 0);
            double ySpacing = A.Num(args, "ySpacing", 0);
            double xStart = A.Num(args, "xStartPosition", 0);
            double yStart = A.Num(args, "yStartPosition", 0);
            double xMin = A.Num(args, "xExtentMin", 0);
            double xMax = A.Num(args, "xExtentMax", 50000);
            double yMin = A.Num(args, "yExtentMin", 0);
            double yMax = A.Num(args, "yExtentMax", 50000);
            double elev = A.Num(args, "elevation", 0);
            string xStyle = A.Str(args, "xNamingStyle", "alphabetic");
            string yStyle = A.Str(args, "yNamingStyle", "numeric");
            string xLabel = A.Str(args, "xStartLabel", "A");
            string yLabel = A.Str(args, "yStartLabel", "1");

            return Rx.Tx<object>(doc, "통심 생성", delegate
            {
                JArray created = new JArray();
                JArray failed = new JArray();

                // X 방향 통심: X 가 고정이고 Y 로 뻗는 세로선
                for (int i = 0; i < xCount; i++)
                {
                    double x = xStart + i * xSpacing;
                    string label = LabelAt(xLabel, i, xStyle);
                    try
                    {
                        Line line = Line.CreateBound(U.PtFt(x, yMin, elev), U.PtFt(x, yMax, elev));
                        Grid g = Grid.Create(doc, line);
                        try { g.Name = label; } catch { }
                        JObject r = new JObject();
                        r["id"] = g.Id.IntegerValue;
                        r["name"] = g.Name;
                        r["axis"] = "X";
                        r["position"] = x;
                        created.Add(r);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new JObject(new JProperty("label", label), new JProperty("error", ex.Message)));
                    }
                }

                // Y 방향 통심: Y 가 고정이고 X 로 뻗는 가로선
                for (int i = 0; i < yCount; i++)
                {
                    double y = yStart + i * ySpacing;
                    string label = LabelAt(yLabel, i, yStyle);
                    try
                    {
                        Line line = Line.CreateBound(U.PtFt(xMin, y, elev), U.PtFt(xMax, y, elev));
                        Grid g = Grid.Create(doc, line);
                        try { g.Name = label; } catch { }
                        JObject r = new JObject();
                        r["id"] = g.Id.IntegerValue;
                        r["name"] = g.Name;
                        r["axis"] = "Y";
                        r["position"] = y;
                        created.Add(r);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new JObject(new JProperty("label", label), new JProperty("error", ex.Message)));
                    }
                }

                JObject res = new JObject();
                res["success"] = failed.Count == 0;
                res["createdCount"] = created.Count;
                res["created"] = created;
                if (failed.Count > 0) res["failed"] = failed;
                return res;
            });
        }

        static object CreateRoom(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            Level level = ResolveLevel(doc, args, true);
            bool autoPlace = A.Bool(args, "autoPlace", false);
            JArray rooms = A.Arr(args, "rooms");

            if (!autoPlace && (rooms == null || rooms.Count == 0))
                throw new ArgumentException("rooms 가 비어 있고 autoPlace 도 꺼져 있어 만들 방이 없습니다.");

            return Rx.Tx<object>(doc, "방 생성", delegate
            {
                JArray created = new JArray();
                JArray failed = new JArray();

                if (autoPlace)
                {
                    PlanTopology topo = doc.get_PlanTopology(level);
                    ICollection<ElementId> made = doc.Create.NewRooms2(level);
                    foreach (ElementId id in made)
                    {
                        Element e = doc.GetElement(id);
                        if (e == null) continue;
                        JObject r = new JObject();
                        r["id"] = id.IntegerValue;
                        r["name"] = e.Name;
                        created.Add(r);
                    }
                }

                if (rooms != null)
                {
                    foreach (JToken t in rooms)
                    {
                        JObject o = t as JObject;
                        if (o == null) continue;
                        try
                        {
                            XYZ p = A.PointFtReq(o, "point");
                            UV uv = new UV(p.X, p.Y);
                            Room room = doc.Create.NewRoom(level, uv);
                            if (room == null) throw new InvalidOperationException(
                                "그 위치에 방을 만들지 못했습니다. 벽으로 닫힌 영역인지 확인하십시오.");

                            string nm = A.Str(o, "name", null);
                            string num = A.Str(o, "number", null);
                            if (!string.IsNullOrEmpty(nm)) { try { room.Name = nm; } catch { } }
                            if (!string.IsNullOrEmpty(num)) { try { room.Number = num; } catch { } }

                            JObject r = new JObject();
                            r["id"] = room.Id.IntegerValue;
                            r["name"] = room.Name;
                            r["number"] = room.Number;
                            r["area_m2"] = Math.Round(room.Area * 0.09290304, 3);
                            created.Add(r);
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new JObject(new JProperty("error", ex.Message)));
                        }
                    }
                }

                JObject res = new JObject();
                res["success"] = failed.Count == 0;
                res["level"] = level.Name;
                res["createdCount"] = created.Count;
                res["created"] = created;
                if (failed.Count > 0) res["failed"] = failed;
                return res;
            });
        }

        static object CreatePointBased(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            JArray items = A.Arr(args, "elements");
            if (items == null || items.Count == 0)
                throw new ArgumentException("배치할 요소 목록(elements)이 비어 있습니다.");

            return Rx.Tx<object>(doc, "요소 배치", delegate
            {
                JArray created = new JArray();
                JArray failed = new JArray();

                foreach (JToken t in items)
                {
                    JObject o = t as JObject;
                    if (o == null) continue;
                    try
                    {
                        FamilySymbol fs = GetSymbol(doc, A.IntReq(o, "typeId"));
                        XYZ p = A.PointFtReq(o, "point");
                        Level lv = ResolveLevel(doc, o, true);

                        FamilyInstance fi;
                        int hostId = A.Int(o, "hostId", -1);
                        if (hostId > 0)
                        {
                            Element host = doc.GetElement(new ElementId(hostId));
                            if (host == null) throw new ArgumentException("hostId " + hostId + " 요소를 찾을 수 없습니다.");
                            fi = doc.Create.NewFamilyInstance(p, fs, host, lv, StructuralType.NonStructural);
                        }
                        else
                        {
                            fi = doc.Create.NewFamilyInstance(p, fs, lv, StructuralType.NonStructural);
                        }

                        double rot = A.Num(o, "rotation", 0);
                        if (Math.Abs(rot) > 1e-9)
                        {
                            Line axis = Line.CreateBound(p, p + XYZ.BasisZ);
                            ElementTransformUtils.RotateElement(doc, fi.Id, axis, rot * Math.PI / 180.0);
                        }

                        JObject r = new JObject();
                        r["id"] = fi.Id.IntegerValue;
                        r["typeName"] = fs.Name;
                        r["familyName"] = fs.FamilyName;
                        created.Add(r);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new JObject(new JProperty("error", ex.Message)));
                    }
                }

                JObject res = new JObject();
                res["success"] = failed.Count == 0;
                res["createdCount"] = created.Count;
                res["created"] = created;
                if (failed.Count > 0) res["failed"] = failed;
                return res;
            });
        }

        static object CreateLineBased(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            JArray items = A.Arr(args, "elements");
            if (items == null || items.Count == 0)
                throw new ArgumentException("만들 요소 목록(elements)이 비어 있습니다.");

            return Rx.Tx<object>(doc, "선형 요소 생성", delegate
            {
                JArray created = new JArray();
                JArray failed = new JArray();

                foreach (JToken t in items)
                {
                    JObject o = t as JObject;
                    if (o == null) continue;
                    try
                    {
                        int typeId = A.IntReq(o, "typeId");
                        XYZ p0 = A.PointFtReq(o, "start");
                        XYZ p1 = A.PointFtReq(o, "end");
                        if (p0.DistanceTo(p1) < 1e-6)
                            throw new ArgumentException("시작점과 끝점이 같습니다.");

                        Level lv = ResolveLevel(doc, o, true);
                        double offsetFt = U.ToFt(A.Num(o, "offset", 0));
                        bool structural = A.Bool(o, "structural", false);

                        Element typeElem = doc.GetElement(new ElementId(typeId));
                        if (typeElem == null) throw new ArgumentException("typeId " + typeId + " 요소를 찾을 수 없습니다.");

                        Element made;
                        WallType wt = typeElem as WallType;
                        if (wt != null)
                        {
                            Line line = Line.CreateBound(
                                new XYZ(p0.X, p0.Y, 0), new XYZ(p1.X, p1.Y, 0));
                            double heightFt = U.ToFt(A.Num(o, "height", 3000));
                            made = Wall.Create(doc, line, wt.Id, lv.Id, heightFt, offsetFt, false, structural);
                        }
                        else
                        {
                            FamilySymbol fs = GetSymbol(doc, typeId);
                            Line line = Line.CreateBound(p0, p1);
                            StructuralType st = structural ? StructuralType.Beam : StructuralType.NonStructural;
                            made = doc.Create.NewFamilyInstance(line, fs, lv, st);
                        }

                        JObject r = new JObject();
                        r["id"] = made.Id.IntegerValue;
                        r["class"] = made.GetType().Name;
                        r["typeName"] = typeElem.Name;
                        created.Add(r);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new JObject(new JProperty("error", ex.Message)));
                    }
                }

                JObject res = new JObject();
                res["success"] = failed.Count == 0;
                res["createdCount"] = created.Count;
                res["created"] = created;
                if (failed.Count > 0) res["failed"] = failed;
                return res;
            });
        }

        static CurveLoop BoundaryLoop(JArray boundary, double zFt)
        {
            if (boundary == null || boundary.Count < 3)
                throw new ArgumentException("경계(boundary)는 점이 3개 이상이어야 합니다.");

            List<XYZ> pts = new List<XYZ>();
            foreach (JToken t in boundary)
            {
                JObject o = t as JObject;
                if (o == null) continue;
                pts.Add(new XYZ(U.ToFt(A.Num(o, "x", 0)), U.ToFt(A.Num(o, "y", 0)), zFt));
            }
            if (pts.Count < 3) throw new ArgumentException("유효한 경계점이 3개 미만입니다.");

            // 마지막 점이 첫 점과 같으면 중복이므로 뺀다.
            if (pts[0].DistanceTo(pts[pts.Count - 1]) < 1e-9) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) throw new ArgumentException("유효한 경계점이 3개 미만입니다.");

            CurveLoop loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                XYZ a = pts[i];
                XYZ b = pts[(i + 1) % pts.Count];
                if (a.DistanceTo(b) < 1e-7)
                    throw new ArgumentException("경계에 길이가 0 인 변이 있습니다 (" + (i + 1) + "번째).");
                loop.Append(Line.CreateBound(a, b));
            }
            return loop;
        }

        static object CreateSurfaceBased(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            string kind = A.Str(args, "elementType", "Floor");
            int typeId = A.IntReq(args, "typeId");
            Level lv = ResolveLevel(doc, args, true);
            double offsetFt = U.ToFt(A.Num(args, "offset", 0));
            JArray boundary = A.Arr(args, "boundary");

            return Rx.Tx<object>(doc, kind + " 생성", delegate
            {
                CurveLoop loop = BoundaryLoop(boundary, 0);
                List<CurveLoop> loops = new List<CurveLoop>();
                loops.Add(loop);

                Element made;
                if (string.Equals(kind, "Ceiling", StringComparison.OrdinalIgnoreCase))
                {
                    made = Ceiling.Create(doc, loops, new ElementId(typeId), lv.Id);
                }
                else if (string.Equals(kind, "Roof", StringComparison.OrdinalIgnoreCase))
                {
                    RoofType rt = doc.GetElement(new ElementId(typeId)) as RoofType;
                    if (rt == null) throw new ArgumentException("typeId " + typeId + " 는 지붕 유형이 아닙니다.");
                    CurveArray ca = new CurveArray();
                    foreach (Curve c in loop) ca.Append(c);
                    ModelCurveArray mca;
                    made = doc.Create.NewFootPrintRoof(ca, lv, rt, out mca);
                }
                else
                {
                    made = Floor.Create(doc, loops, new ElementId(typeId), lv.Id);
                }

                if (Math.Abs(offsetFt) > 1e-9)
                {
                    Parameter p = made.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                    if (p != null && !p.IsReadOnly) p.Set(offsetFt);
                }

                JObject res = new JObject();
                res["success"] = true;
                res["id"] = made.Id.IntegerValue;
                res["class"] = made.GetType().Name;
                res["level"] = lv.Name;
                return res;
            });
        }

        static object CreateFramingSystem(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            int beamTypeId = A.IntReq(args, "beamTypeId");
            double spacingMm = A.NumReq(args, "spacing");
            if (spacingMm <= 0) throw new ArgumentException("spacing 은 0 보다 커야 합니다.");

            Level lv = ResolveLevel(doc, args, true);
            double offsetFt = U.ToFt(A.Num(args, "offset", 0));
            string dir = A.Str(args, "direction", "x").ToLowerInvariant();
            JArray boundary = A.Arr(args, "boundary");
            if (boundary == null || boundary.Count < 3)
                throw new ArgumentException("경계(boundary)는 점이 3개 이상이어야 합니다.");

            // 경계의 최소·최대만 써서 직사각형 영역으로 다룬다.
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (JToken t in boundary)
            {
                JObject o = t as JObject;
                if (o == null) continue;
                double x = A.Num(o, "x", 0), y = A.Num(o, "y", 0);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            return Rx.Tx<object>(doc, "보 시스템 생성", delegate
            {
                FamilySymbol fs = GetSymbol(doc, beamTypeId);
                double zFt = lv.Elevation + offsetFt;

                JArray created = new JArray();
                JArray failed = new JArray();

                bool alongX = (dir == "x");
                double from = alongX ? minY : minX;
                double to = alongX ? maxY : maxX;

                for (double pos = from; pos <= to + 1e-6; pos += spacingMm)
                {
                    try
                    {
                        XYZ a, b;
                        if (alongX)
                        {
                            a = new XYZ(U.ToFt(minX), U.ToFt(pos), zFt);
                            b = new XYZ(U.ToFt(maxX), U.ToFt(pos), zFt);
                        }
                        else
                        {
                            a = new XYZ(U.ToFt(pos), U.ToFt(minY), zFt);
                            b = new XYZ(U.ToFt(pos), U.ToFt(maxY), zFt);
                        }
                        if (a.DistanceTo(b) < 1e-6) continue;

                        FamilyInstance beam = doc.Create.NewFamilyInstance(
                            Line.CreateBound(a, b), fs, lv, StructuralType.Beam);
                        created.Add(beam.Id.IntegerValue);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new JObject(new JProperty("position", pos), new JProperty("error", ex.Message)));
                    }
                }

                JObject res = new JObject();
                res["success"] = failed.Count == 0;
                res["beamCount"] = created.Count;
                res["direction"] = dir;
                res["spacing"] = spacingMm;
                res["level"] = lv.Name;
                res["createdIds"] = created;
                if (failed.Count > 0) res["failed"] = failed;
                return res;
            });
        }
    }
}
