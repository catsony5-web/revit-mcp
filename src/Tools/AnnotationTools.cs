using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    internal static class AnnotationTools
    {
        public static void Register()
        {
            ToolRegistry.Register("create_dimensions",
                "현재 뷰에 치수를 넣는다. elementIds 로 대상 요소를 지정하는 쪽이 확실하다. 지정하지 않으면 " +
                "시작점·끝점 근처에 있는 선형 요소(벽, 통심 등)를 찾아 참조로 쓴다. 좌표는 mm.",
                S.Obj()
                    .ObjArr("dimensions", "만들 치수 목록",
                        S.Obj()
                            .Pt("startPoint", "치수 시작점 (mm)", true)
                            .Pt("endPoint", "치수 끝점 (mm)", true)
                            .Pt("linePoint", "치수선이 놓일 위치 (mm). 생략하면 두 점의 중간에서 조금 띄운다.", false)
                            .IntArr("elementIds", "치수를 잴 요소의 ElementId 목록", false)
                            .Int("dimensionStyleId", "적용할 치수 유형의 ElementId. 생략하면 기본 유형.")
                            .Int("viewId", "치수를 넣을 뷰의 ElementId. 생략하면 활성 뷰."),
                        true)
                    .NumDef("searchTolerance", "elementIds 없이 자동으로 찾을 때의 탐색 반경 (mm)", 500),
                CreateDimensions);

            ToolRegistry.Register("tag_all_rooms",
                "현재 뷰의 모든 방에 방 태그를 단다. 이미 태그가 있는 방은 건너뛴다.",
                S.Obj()
                    .Int("viewId", "대상 뷰의 ElementId. 생략하면 활성 뷰.")
                    .Int("tagTypeId", "쓸 방 태그 유형의 ElementId. 생략하면 첫 번째 방 태그 유형.")
                    .Bool("skipTagged", "이미 태그가 달린 방은 건너뛸지", true),
                TagAllRooms);

            ToolRegistry.Register("tag_all_walls",
                "현재 뷰의 모든 벽에 벽 태그를 단다. 이미 태그가 있는 벽은 건너뛴다.",
                S.Obj()
                    .Int("viewId", "대상 뷰의 ElementId. 생략하면 활성 뷰.")
                    .Int("tagTypeId", "쓸 벽 태그 유형의 ElementId. 생략하면 첫 번째 벽 태그 유형.")
                    .Bool("addLeader", "지시선을 붙일지", false)
                    .Bool("skipTagged", "이미 태그가 달린 벽은 건너뛸지", true),
                TagAllWalls);
        }

        static View ResolveView(Document doc, JObject o)
        {
            int vid = A.Int(o, "viewId", -1);
            if (vid > 0)
            {
                View v = doc.GetElement(new ElementId(vid)) as View;
                if (v == null) throw new ArgumentException("viewId " + vid + " 는 뷰가 아닙니다.");
                return v;
            }
            View av = doc.ActiveView;
            if (av == null) throw new InvalidOperationException("활성 뷰가 없습니다.");
            return av;
        }

        static object CreateDimensions(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            JArray dims = A.Arr(args, "dimensions");
            if (dims == null || dims.Count == 0)
                throw new ArgumentException("만들 치수 목록(dimensions)이 비어 있습니다.");

            double tolFt = U.ToFt(A.Num(args, "searchTolerance", 500));

            return Rx.Tx<object>(doc, "치수 생성", delegate
            {
                JArray created = new JArray();
                JArray failed = new JArray();

                foreach (JToken t in dims)
                {
                    JObject o = t as JObject;
                    if (o == null) continue;
                    try
                    {
                        View view = ResolveView(doc, o);
                        XYZ p0 = A.PointFtReq(o, "startPoint");
                        XYZ p1 = A.PointFtReq(o, "endPoint");
                        if (p0.DistanceTo(p1) < 1e-6)
                            throw new ArgumentException("시작점과 끝점이 같습니다.");

                        ReferenceArray refs = new ReferenceArray();
                        List<ElementId> ids = A.ElementIds(o, "elementIds");

                        if (ids.Count > 0)
                        {
                            foreach (ElementId id in ids)
                            {
                                Element e = doc.GetElement(id);
                                if (e == null) continue;
                                Reference eref = GetReference(e);
                                if (eref != null) refs.Append(eref);
                            }
                        }
                        else
                        {
                            Reference r0 = FindNearbyReference(doc, view, p0, tolFt);
                            Reference r1 = FindNearbyReference(doc, view, p1, tolFt);
                            if (r0 != null) refs.Append(r0);
                            if (r1 != null && (r0 == null || r1.ElementId != r0.ElementId)) refs.Append(r1);
                        }

                        if (refs.Size < 2)
                            throw new InvalidOperationException(
                                "치수를 잴 참조를 2개 이상 찾지 못했습니다. elementIds 로 대상을 직접 지정하십시오.");

                        XYZ linePt = A.PointFt(o["linePoint"]);
                        if (linePt == null)
                        {
                            XYZ mid = (p0 + p1) / 2.0;
                            XYZ dir = (p1 - p0).Normalize();
                            XYZ perp = new XYZ(-dir.Y, dir.X, 0);
                            if (perp.GetLength() < 1e-9) perp = XYZ.BasisY;
                            linePt = mid + perp.Normalize() * U.ToFt(1000);
                        }

                        Line dimLine = Line.CreateBound(
                            linePt + (p0 - (p0 + p1) / 2.0),
                            linePt + (p1 - (p0 + p1) / 2.0));

                        Dimension d;
                        int styleId = A.Int(o, "dimensionStyleId", -1);
                        if (styleId > 0)
                        {
                            DimensionType dt = doc.GetElement(new ElementId(styleId)) as DimensionType;
                            if (dt == null) throw new ArgumentException("dimensionStyleId " + styleId + " 는 치수 유형이 아닙니다.");
                            d = doc.Create.NewDimension(view, dimLine, refs, dt);
                        }
                        else
                        {
                            d = doc.Create.NewDimension(view, dimLine, refs);
                        }

                        JObject r = new JObject();
                        r["id"] = d.Id.IntegerValue;
                        r["view"] = view.Name;
                        r["referenceCount"] = refs.Size;
                        try
                        {
                            if (d.Value.HasValue) r["value_mm"] = Math.Round(U.ToMm(d.Value.Value), 2);
                        }
                        catch { }
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

        static Reference GetReference(Element e)
        {
            // 선형 요소는 중심선 참조가 가장 안정적이다.
            LocationCurve lc = e.Location as LocationCurve;
            if (lc != null && lc.Curve != null && lc.Curve.Reference != null)
                return lc.Curve.Reference;

            Grid g = e as Grid;
            if (g != null)
            {
                IList<Curve> cs = g.GetCurvesInView(DatumExtentType.Model, null);
                if (cs != null && cs.Count > 0 && cs[0].Reference != null) return cs[0].Reference;
            }

            // 마지막 수단: 기하에서 평면을 하나 뽑는다.
            Options opt = new Options();
            opt.ComputeReferences = true;
            opt.IncludeNonVisibleObjects = false;
            GeometryElement ge = e.get_Geometry(opt);
            if (ge != null)
            {
                foreach (GeometryObject go in ge)
                {
                    Solid s = go as Solid;
                    if (s == null) continue;
                    foreach (Face f in s.Faces)
                    {
                        if (f is PlanarFace && f.Reference != null) return f.Reference;
                    }
                }
            }
            return null;
        }

        static Reference FindNearbyReference(Document doc, View view, XYZ pt, double tolFt)
        {
            Element best = null;
            double bestDist = double.MaxValue;

            foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                LocationCurve lc = e.Location as LocationCurve;
                if (lc == null || lc.Curve == null) continue;
                try
                {
                    IntersectionResult ir = lc.Curve.Project(pt);
                    if (ir == null) continue;
                    double d = ir.XYZPoint.DistanceTo(pt);
                    if (d < bestDist && d <= tolFt) { bestDist = d; best = e; }
                }
                catch { }
            }
            return best != null ? GetReference(best) : null;
        }

        static FamilySymbol FindTagType(Document doc, BuiltInCategory cat, int requestedId)
        {
            if (requestedId > 0)
            {
                FamilySymbol fs = doc.GetElement(new ElementId(requestedId)) as FamilySymbol;
                if (fs == null) throw new ArgumentException("tagTypeId " + requestedId + " 는 태그 유형이 아닙니다.");
                if (!fs.IsActive) fs.Activate();
                return fs;
            }

            foreach (Element e in new FilteredElementCollector(doc)
                        .OfCategory(cat).WhereElementIsElementType())
            {
                FamilySymbol fs = e as FamilySymbol;
                if (fs == null) continue;
                if (!fs.IsActive) fs.Activate();
                return fs;
            }
            return null;
        }

        static object TagAllRooms(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            View view = ResolveView(doc, args);
            bool skipTagged = A.Bool(args, "skipTagged", true);

            // 이미 태그가 달린 방을 모아둔다.
            HashSet<int> tagged = new HashSet<int>();
            if (skipTagged)
            {
                foreach (Element e in new FilteredElementCollector(doc, view.Id)
                            .OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType())
                {
                    RoomTag rt = e as RoomTag;
                    if (rt != null && rt.Room != null) tagged.Add(rt.Room.Id.IntegerValue);
                }
            }

            List<Room> rooms = new List<Room>();
            foreach (Element e in new FilteredElementCollector(doc, view.Id)
                        .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
            {
                Room r = e as Room;
                if (r == null || r.Area <= 0) continue;
                if (skipTagged && tagged.Contains(r.Id.IntegerValue)) continue;
                rooms.Add(r);
            }

            return Rx.Tx<object>(doc, "방 태그 달기", delegate
            {
                FamilySymbol tagType = FindTagType(doc, BuiltInCategory.OST_RoomTags, A.Int(args, "tagTypeId", -1));

                JArray created = new JArray();
                JArray failed = new JArray();

                foreach (Room r in rooms)
                {
                    try
                    {
                        LocationPoint lp = r.Location as LocationPoint;
                        if (lp == null) throw new InvalidOperationException("방의 위치점을 찾을 수 없습니다.");
                        UV uv = new UV(lp.Point.X, lp.Point.Y);

                        RoomTag tag = doc.Create.NewRoomTag(new LinkElementId(r.Id), uv, view.Id);
                        if (tag == null) throw new InvalidOperationException("태그를 만들지 못했습니다.");
                        if (tagType != null)
                        {
                            try { tag.ChangeTypeId(tagType.Id); } catch { }
                        }
                        created.Add(tag.Id.IntegerValue);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new JObject(
                            new JProperty("roomId", r.Id.IntegerValue),
                            new JProperty("error", ex.Message)));
                    }
                }

                JObject res = new JObject();
                res["success"] = failed.Count == 0;
                res["view"] = view.Name;
                res["candidateRooms"] = rooms.Count;
                res["taggedCount"] = created.Count;
                res["skippedAlreadyTagged"] = tagged.Count;
                if (failed.Count > 0) res["failed"] = failed;
                return res;
            });
        }

        static object TagAllWalls(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            View view = ResolveView(doc, args);
            bool skipTagged = A.Bool(args, "skipTagged", true);
            bool addLeader = A.Bool(args, "addLeader", false);

            HashSet<int> tagged = new HashSet<int>();
            if (skipTagged)
            {
                foreach (Element e in new FilteredElementCollector(doc, view.Id)
                            .OfCategory(BuiltInCategory.OST_WallTags).WhereElementIsNotElementType())
                {
                    IndependentTag it = e as IndependentTag;
                    if (it == null) continue;
                    try
                    {
                        foreach (ElementId id in it.GetTaggedLocalElementIds()) tagged.Add(id.IntegerValue);
                    }
                    catch { }
                }
            }

            List<Wall> walls = new List<Wall>();
            foreach (Element e in new FilteredElementCollector(doc, view.Id)
                        .OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType())
            {
                Wall w = e as Wall;
                if (w == null) continue;
                if (skipTagged && tagged.Contains(w.Id.IntegerValue)) continue;
                walls.Add(w);
            }

            return Rx.Tx<object>(doc, "벽 태그 달기", delegate
            {
                FamilySymbol tagType = FindTagType(doc, BuiltInCategory.OST_WallTags, A.Int(args, "tagTypeId", -1));
                if (tagType == null)
                    throw new InvalidOperationException(
                        "프로젝트에 벽 태그 패밀리가 없습니다. 먼저 벽 태그 패밀리를 로드하십시오.");

                JArray created = new JArray();
                JArray failed = new JArray();

                foreach (Wall w in walls)
                {
                    try
                    {
                        LocationCurve lc = w.Location as LocationCurve;
                        if (lc == null || lc.Curve == null) throw new InvalidOperationException("벽의 중심선을 찾을 수 없습니다.");
                        XYZ mid = lc.Curve.Evaluate(0.5, true);

                        IndependentTag tag = IndependentTag.Create(
                            doc, tagType.Id, view.Id, new Reference(w), addLeader, TagOrientation.Horizontal, mid);
                        created.Add(tag.Id.IntegerValue);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new JObject(
                            new JProperty("wallId", w.Id.IntegerValue),
                            new JProperty("error", ex.Message)));
                    }
                }

                JObject res = new JObject();
                res["success"] = failed.Count == 0;
                res["view"] = view.Name;
                res["candidateWalls"] = walls.Count;
                res["taggedCount"] = created.Count;
                res["skippedAlreadyTagged"] = tagged.Count;
                if (failed.Count > 0) res["failed"] = failed;
                return res;
            });
        }
    }
}
