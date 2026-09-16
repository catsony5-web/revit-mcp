using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    internal static class RoomTools
    {
        internal const string RoomsCollection = "rooms";

        public static void Register()
        {
            ToolRegistry.Register("export_room_data",
                "Revit 모델에서 방 정보를 뽑아낸다. 이름, 번호, 레벨, 면적(m2), 둘레(m), 부서, 용도를 준다. " +
                "savePath 를 주면 파일로도 떨군다. 면적은 m2, 둘레는 m 단위다.",
                S.Obj()
                    .Str("levelName", "이 레벨의 방만. 비우면 전체.")
                    .Bool("currentViewOnly", "현재 뷰에 보이는 방만 뽑을지", false)
                    .Bool("includeBoundary", "방 경계 다각형 좌표(mm)까지 포함할지", false)
                    .Bool("includeParameters", "방의 모든 파라미터를 포함할지", false)
                    .Enum("format", "savePath 로 저장할 때의 형식", new string[] { "json", "csv" }, "json")
                    .Str("savePath", "결과를 저장할 파일 경로 (선택). 폴더는 미리 있어야 한다.")
                    .IntDef("limit", "돌려줄 최대 방 개수", 500),
                ExportRoomData);

            ToolRegistry.Register("store_room_data",
                "방 정보를 로컬 저장소에 기록한다. 프로젝트 이름으로 묶이며 그 프로젝트가 먼저 저장돼 있어야 한다. " +
                "Revit 모델을 바꾸지 않는다.",
                S.Obj()
                    .StrReq("project_name", "이 방들이 속한 프로젝트 이름. store_project_data 로 먼저 등록해야 한다.")
                    .ObjArr("rooms", "저장할 방 목록",
                        S.Obj()
                            .StrReq("room_id", "방의 고유 식별자. 보통 Revit ElementId.")
                            .Str("room_name", "방 이름")
                            .Str("room_number", "방 번호")
                            .Str("department", "부서")
                            .Str("level", "층 또는 레벨")
                            .Num("area", "면적")
                            .Num("perimeter", "둘레")
                            .Str("occupancy", "용도")
                            .Str("comments", "비고")
                            .Any("metadata", "그 밖에 남길 값들"),
                        true),
                StoreRoomData, false);
        }

        const double Ft2ToM2 = 0.09290304;
        const double FtToM = 0.3048;

        static object ExportRoomData(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);

            string levelName = A.Str(args, "levelName", null);
            bool currentViewOnly = A.Bool(args, "currentViewOnly", false);
            bool withBoundary = A.Bool(args, "includeBoundary", false);
            bool withParams = A.Bool(args, "includeParameters", false);
            int limit = A.Int(args, "limit", 500);

            FilteredElementCollector col = currentViewOnly && doc.ActiveView != null
                ? new FilteredElementCollector(doc, doc.ActiveView.Id)
                : new FilteredElementCollector(doc);

            col = col.OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType();

            JArray rooms = new JArray();
            int total = 0;
            int unplaced = 0;
            double totalArea = 0;

            foreach (Element e in col)
            {
                Room r = e as Room;
                if (r == null) continue;

                if (r.Area <= 0) { unplaced++; continue; }

                string lvl = null;
                if (r.LevelId != null && r.LevelId != ElementId.InvalidElementId)
                {
                    Element lv = doc.GetElement(r.LevelId);
                    if (lv != null) lvl = lv.Name;
                }
                if (!string.IsNullOrEmpty(levelName) &&
                    !string.Equals(lvl, levelName, StringComparison.OrdinalIgnoreCase)) continue;

                total++;
                totalArea += r.Area * Ft2ToM2;
                if (rooms.Count >= limit) continue;

                JObject o = new JObject();
                o["room_id"] = r.Id.IntegerValue.ToString();
                o["room_name"] = r.Name;
                o["room_number"] = r.Number;
                o["level"] = lvl;
                o["area_m2"] = Math.Round(r.Area * Ft2ToM2, 3);
                o["perimeter_m"] = Math.Round(r.Perimeter * FtToM, 3);
                o["volume_m3"] = Math.Round(r.Volume * 0.0283168466, 3);
                o["department"] = SafeParam(r, BuiltInParameter.ROOM_DEPARTMENT);
                o["occupancy"] = SafeParam(r, BuiltInParameter.ROOM_OCCUPANCY);
                o["comments"] = SafeParam(r, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);

                LocationPoint lp = r.Location as LocationPoint;
                if (lp != null) o["location"] = Rx.PtJson(lp.Point);

                if (withBoundary) o["boundary"] = BoundaryOf(r);
                if (withParams)
                {
                    JObject ps = new JObject();
                    foreach (Parameter p in r.Parameters)
                    {
                        try
                        {
                            if (p == null || p.Definition == null) continue;
                            string key = p.Definition.Name;
                            if (ps[key] != null) continue;
                            ps[key] = Rx.ParamValue(p);
                        }
                        catch { }
                    }
                    o["parameters"] = ps;
                }

                rooms.Add(o);
            }

            JObject res = new JObject();
            res["documentTitle"] = doc.Title;
            res["totalRooms"] = total;
            res["returned"] = rooms.Count;
            res["unplacedRooms"] = unplaced;
            res["totalArea_m2"] = Math.Round(totalArea, 3);
            if (total > rooms.Count)
                res["note"] = "총 " + total + "개 중 " + rooms.Count + "개만 돌려주었습니다. limit 를 올리십시오.";
            res["rooms"] = rooms;

            string savePath = A.Str(args, "savePath", null);
            if (!string.IsNullOrEmpty(savePath))
            {
                string format = A.Str(args, "format", "json").ToLowerInvariant();
                try
                {
                    if (format == "csv") File.WriteAllText(savePath, ToCsv(rooms), new UTF8Encoding(true));
                    else File.WriteAllText(savePath, res.ToString(Formatting.Indented), new UTF8Encoding(false));
                    res["savedTo"] = savePath;
                }
                catch (Exception ex)
                {
                    res["saveError"] = ex.Message;
                }
            }

            if (total == 0)
                res["message"] = "방이 하나도 잡히지 않았습니다. 모델에 방이 배치돼 있는지, levelName 이 맞는지 확인하십시오.";
            return res;
        }

        static string SafeParam(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue) return null;
                return p.AsString();
            }
            catch { return null; }
        }

        static JArray BoundaryOf(Room r)
        {
            JArray loops = new JArray();
            try
            {
                SpatialElementBoundaryOptions opt = new SpatialElementBoundaryOptions();
                opt.SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish;
                IList<IList<BoundarySegment>> segs = r.GetBoundarySegments(opt);
                if (segs == null) return loops;

                foreach (IList<BoundarySegment> loop in segs)
                {
                    JArray pts = new JArray();
                    foreach (BoundarySegment s in loop)
                    {
                        Curve c = s.GetCurve();
                        if (c == null) continue;
                        pts.Add(Rx.PtJson(c.GetEndPoint(0)));
                    }
                    loops.Add(pts);
                }
            }
            catch { }
            return loops;
        }

        static string ToCsv(JArray rooms)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("room_id,room_name,room_number,level,area_m2,perimeter_m,department,occupancy,comments");
            foreach (JToken t in rooms)
            {
                JObject o = t as JObject;
                if (o == null) continue;
                sb.AppendLine(string.Join(",", new string[]
                {
                    Csv(o["room_id"]), Csv(o["room_name"]), Csv(o["room_number"]), Csv(o["level"]),
                    Csv(o["area_m2"]), Csv(o["perimeter_m"]), Csv(o["department"]),
                    Csv(o["occupancy"]), Csv(o["comments"])
                }));
            }
            return sb.ToString();
        }

        static string Csv(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "";
            string s = t.ToString();
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        static object StoreRoomData(UIApplication uiapp, JObject args)
        {
            string projectName = A.StrReq(args, "project_name");

            JObject project = null;
            foreach (JToken t in DataStore.Load("projects"))
            {
                JObject rec = t as JObject;
                if (rec == null) continue;
                if (string.Equals((string)rec["key"], projectName, StringComparison.OrdinalIgnoreCase))
                {
                    project = rec["value"] as JObject;
                    break;
                }
            }
            if (project == null)
                throw new ArgumentException("'" + projectName + "' 프로젝트가 저장소에 없습니다. " +
                                            "store_project_data 로 먼저 등록하십시오.");

            int projectId = (int)project["id"];
            JArray rooms = A.Arr(args, "rooms");
            if (rooms == null || rooms.Count == 0)
                throw new ArgumentException("저장할 방 목록(rooms)이 비어 있습니다.");

            int stored = 0;
            JArray failed = new JArray();

            foreach (JToken t in rooms)
            {
                JObject o = t as JObject;
                if (o == null) continue;
                try
                {
                    string roomId = A.StrReq(o, "room_id");
                    JObject rec = new JObject(o);
                    rec["project_id"] = projectId;
                    rec["project_name"] = projectName;

                    // 프로젝트가 달라도 겹치지 않도록 키를 조합한다.
                    DataStore.Put(RoomsCollection, projectId + ":" + roomId, rec, null);
                    stored++;
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject(new JProperty("error", ex.Message)));
                }
            }

            JObject res = new JObject();
            res["success"] = failed.Count == 0;
            res["message"] = stored + "개 방을 저장했습니다.";
            res["project_id"] = projectId;
            res["project_name"] = projectName;
            res["rooms_stored"] = stored;
            res["total_rooms"] = DataTools.RoomsOfProject(projectId).Count;
            res["storagePath"] = DataStore.Root;
            if (failed.Count > 0) res["failed"] = failed;
            return res;
        }
    }
}
