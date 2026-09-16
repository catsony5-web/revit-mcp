using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    internal static class CadTools
    {
        static readonly Guid MarkerGuid = new Guid("eb8710dd-e0ba-4a6e-9163-6bca5e5165db");
        const string PlanVersion = "cad-layout-1";

        public static void Register()
        {
            ToolRegistry.Register("inspect_cad_folder",
                "Inspect a bounded local folder of DWG/DXF files without accessing Revit. Classify discipline/floor candidates, backups and ambiguous drawings from names. Does not decode drawing geometry.",
                S.Obj().StrReq("root", "Absolute local folder path")
                    .IntDef("maxFiles", "Maximum CAD files, 1..2000", 500).IntDef("maxDepth", "Folder depth, 0..16", 8), Inspect, false);
            ToolRegistry.Register("plan_cad_layout",
                "Read levels/views and prepare a reviewable per-floor CAD placement plan. Does not modify the model. Explicit units and alignment are required; ambiguous or multi-floor drawings remain unresolved.",
                S.Obj().StrReq("root", "Absolute local folder")
                    .IntDef("maxFiles", "Maximum files inspected, 1..2000", 500)
                    .Bool("onlyMappedFiles", "Include only files named in mappings", false)
                    .Enum("unit", "Explicit drawing unit; no silent auto-detection", new[] { "millimeter", "centimeter", "meter", "inch", "foot" }, null)
                    .Bool("confirmSharedOrigin", "All selected drawings use matching XY model-space origins and axes; use zero XY translation/rotation", false)
                    .Bool("createMissingLevels", "Allow new levels only where mapping supplies elevationMm", false)
                    .Bool("createMissingViews", "Allow new dedicated floor plan views", false)
                    .Bool("thisViewOnly", "Link as a view-specific floor underlay. False creates model-space instances associated with the target level", true)
                    .ObjArr("mappings", "Explicit file selections and placement overrides", S.Obj()
                        .StrReq("relativePath", "Drawing under root").Bool("include", "Include this file", true)
                        .Str("floor", "Exact level name, or unambiguous floor label (B1, 1F, RF)")
                        .Int("levelId", "Existing level ID").Int("viewId", "Existing floor/engineering plan view on the chosen level")
                        .Num("elevationMm", "Required for creating a missing level; exact target elevation in mm")
                        .Str("viewName", "Name for an explicitly permitted new view")
                        .Str("unit", "millimeter, centimeter, meter, inch, foot")
                        .Pt("sourceAnchorMm", "Known CAD anchor expressed in mm after the chosen unit conversion", false)
                        .Pt("targetAnchorMm", "Revit internal-coordinate anchor in mm; z must equal the target floor elevation", false)
                        .Num("rotationDegrees", "Counterclockwise XY rotation about the source anchor")
                        .Bool("allowNonPlan", "Explicitly permit a detail/section/unclassified file", false)
                        .Bool("singleFloorConfirmed", "User verified this file contains only the selected floor despite a multi-floor filename", false), false), Plan);
            ToolRegistry.Register("apply_cad_layout",
                "Validate or apply an explicit plan_cad_layout result. dryRun defaults true. Refuses unresolved/stale plans, rolls the complete batch back on any error, and identifies previous placements to prevent duplicates. No save or sync.",
                S.Obj().Any("plan", "Complete plan_cad_layout result, including document identity and items")
                    .Bool("dryRun", "Validate and report without changing Revit", true), Apply);
        }

        static object Inspect(UIApplication uiapp, JObject args)
        {
            return CadPlanning.Inspect(A.StrReq(args, "root"), A.Int(args, "maxFiles", 500), A.Int(args, "maxDepth", 8));
        }

        static JObject DocumentIdentity(Document doc)
        {
            return new JObject(new JProperty("projectUniqueId", doc.ProjectInformation.UniqueId), new JProperty("title", doc.Title), new JProperty("path", doc.PathName));
        }

        static List<Level> Levels(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        }

        static List<ViewPlan> Views(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(delegate(ViewPlan v) { return !v.IsTemplate && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.EngineeringPlan); }).ToList();
        }

        static object Plan(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            if (doc.IsFamilyDocument) throw new ArgumentException("CAD floor layout requires a project document.");
            JObject inspection = CadPlanning.Inspect(A.StrReq(args, "root"), A.Int(args, "maxFiles", 500), 12);
            string root = (string)inspection["root"];
            List<Level> levels = Levels(doc); List<ViewPlan> views = Views(doc);
            long plannedBytes = 0;
            JArray unresolved = new JArray(); JArray items = new JArray(); JArray excluded = new JArray();
            Dictionary<string, JObject> mappings = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken tok in A.Arr(args, "mappings") ?? new JArray())
            {
                JObject mapping = tok as JObject;
                if (mapping == null) throw new ArgumentException("Every mapping must be an object.");
                string full = CadPlanning.UnderRoot(root, A.StrReq(mapping, "relativePath"));
                if (mappings.ContainsKey(full)) throw new ArgumentException("Duplicate mapping. Multi-floor files must first be split into one model-space drawing per floor: " + full);
                mappings.Add(full, mapping);
            }
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject file in (JArray)inspection["files"])
            {
                string relative = (string)file["relativePath"];
                string full = CadPlanning.UnderRoot(root, relative); seen.Add(full);
                JObject mapping; bool explicitMapping = mappings.TryGetValue(full, out mapping);
                if (mapping == null) mapping = new JObject();
                if ((A.Bool(args, "onlyMappedFiles", false) && !explicitMapping) || !A.Bool(mapping, "include", true))
                { excluded.Add(relative); continue; }
                string problem = CadPlanning.BlockingReason(file, explicitMapping, A.Bool(mapping, "allowNonPlan", false), A.Bool(mapping, "singleFloorConfirmed", false));
                string floor = A.Str(mapping, "floor", (string)file["floor"]);
                Level level = null;
                if (problem == null)
                {
                    if (A.Has(mapping, "levelId")) level = doc.GetElement(new ElementId(A.IntReq(mapping, "levelId"))) as Level;
                    else if (!string.IsNullOrWhiteSpace(floor))
                    {
                        List<Level> exact = levels.Where(delegate(Level l) { return string.Equals(l.Name, floor, StringComparison.OrdinalIgnoreCase); }).ToList();
                        if (exact.Count == 1) level = exact[0];
                        else
                        {
                            JArray desired = CadPlanning.Floors(floor);
                            List<Level> matching = levels.Where(delegate(Level l) {
                                JArray candidate = CadPlanning.Floors(l.Name);
                                return desired.Count == 1 && candidate.Count == 1 && (string)desired[0] == (string)candidate[0];
                            }).ToList();
                            if (matching.Count == 1) level = matching[0];
                            else if (matching.Count > 1) problem = "AMBIGUOUS_LEVEL: provide levelId.";
                        }
                    }
                    if (A.Has(mapping, "levelId") && level == null) problem = "INVALID_LEVEL_ID";
                    else if (level == null && (string.IsNullOrWhiteSpace(floor) || !A.Bool(args, "createMissingLevels", false) || !A.Has(mapping, "elevationMm")))
                        problem = "LEVEL_REQUIRED: choose an existing level or explicitly allow a named new level with elevationMm.";
                }
                double elevation = level != null ? U.ToMm(level.Elevation) : A.Num(mapping, "elevationMm", 0);
                if (problem == null && level != null && A.Has(mapping, "elevationMm") && Math.Abs(A.NumReq(mapping, "elevationMm") - elevation) > 0.1)
                    problem = "ELEVATION_MISMATCH: existing level does not match elevationMm.";
                string unit = A.Str(mapping, "unit", A.Str(args, "unit", null));
                if (problem == null && !ValidUnit(unit)) problem = "UNIT_REQUIRED: specify millimeter, centimeter, meter, inch or foot.";
                JObject source = A.Obj(mapping, "sourceAnchorMm"); JObject target = A.Obj(mapping, "targetAnchorMm");
                if (problem == null && (source == null || target == null))
                {
                    if (!A.Bool(args, "confirmSharedOrigin", false)) problem = "ALIGNMENT_REQUIRED: provide sourceAnchorMm and targetAnchorMm, or confirmSharedOrigin.";
                    else if (source != null || target != null) problem = "INCOMPLETE_ANCHOR_PAIR";
                    else { source = Point(0, 0, 0); target = Point(0, 0, elevation); }
                }
                ViewPlan view = null;
                string viewName = A.Str(mapping, "viewName", "MCP CAD - " + (level != null ? level.Name : floor) + " - " + (string)file["discipline"]);
                if (problem == null)
                {
                    if (A.Has(mapping, "viewId"))
                    {
                        view = doc.GetElement(new ElementId(A.IntReq(mapping, "viewId"))) as ViewPlan;
                        if (view == null || view.IsTemplate || level == null || view.GenLevel == null || view.GenLevel.Id != level.Id ||
                            (view.ViewType != ViewType.FloorPlan && view.ViewType != ViewType.EngineeringPlan)) problem = "INVALID_VIEW: choose a floor/engineering plan belonging to the target level.";
                    }
                    else if (level != null)
                    {
                        List<ViewPlan> matching = views.Where(delegate(ViewPlan v) { return v.GenLevel != null && v.GenLevel.Id == level.Id && v.Name == viewName; }).ToList();
                        if (matching.Count == 1) view = matching[0];
                        else
                        {
                            matching = views.Where(delegate(ViewPlan v) { return v.GenLevel != null && v.GenLevel.Id == level.Id; }).ToList();
                            if (matching.Count == 1 && !A.Has(mapping, "viewName")) view = matching[0];
                        }
                    }
                    if (view == null && !A.Bool(args, "createMissingViews", false) && problem == null) problem = "VIEW_REQUIRED: provide viewId or allow a new dedicated floor plan view.";
                }
                if (problem == null)
                {
                    try
                    {
                        ValidatePoint(source, "sourceAnchorMm"); ValidatePoint(target, "targetAnchorMm");
                        if (Math.Abs(A.NumReq(target, "z") - elevation) > 0.1) problem = "TARGET_Z_MISMATCH: targetAnchorMm.z must equal the floor elevation in mm.";
                        double rotation = A.Num(mapping, "rotationDegrees", 0); Finite(rotation, "rotationDegrees");
                    }
                    catch (ArgumentException ex) { problem = ex.Message; }
                }
                if (problem == null)
                {
                    long bytes = file["sizeBytes"].Value<long>();
                    if (bytes > CadPlanning.MaxFileBytes) problem = "FILE_TOO_LARGE: split/reduce drawings exceeding 256 MiB.";
                    else if (plannedBytes + bytes > CadPlanning.MaxBatchBytes) problem = "BATCH_TOO_LARGE: select at most 1 GiB of drawing content per plan.";
                    else plannedBytes += bytes;
                }
                if (problem != null) { unresolved.Add(new JObject(new JProperty("relativePath", relative), new JProperty("reason", problem), new JProperty("inference", file))); continue; }
                JObject item = new JObject(new JProperty("relativePath", relative), new JProperty("fingerprint", CadPlanning.Fingerprint(full)),
                    new JProperty("levelId", level != null ? level.Id.IntegerValue : 0), new JProperty("levelName", level != null ? level.Name : floor),
                    new JProperty("elevationMm", elevation), new JProperty("createLevel", level == null),
                    new JProperty("viewId", view != null ? view.Id.IntegerValue : 0), new JProperty("viewName", view != null ? view.Name : viewName),
                    new JProperty("createView", view == null), new JProperty("unit", unit),
                    new JProperty("sourceAnchorMm", source.DeepClone()), new JProperty("targetAnchorMm", target.DeepClone()),
                    new JProperty("rotationDegrees", A.Num(mapping, "rotationDegrees", 0)), new JProperty("thisViewOnly", A.Bool(args, "thisViewOnly", true)),
                    new JProperty("discipline", file["discipline"]), new JProperty("mode", "link"));
                items.Add(item);
            }
            foreach (string missing in mappings.Keys.Where(delegate(string p) { return !seen.Contains(p); }))
                unresolved.Add(new JObject(new JProperty("relativePath", mappings[missing]["relativePath"]), new JProperty("reason", "MAPPED_FILE_NOT_INSPECTED: missing, unsupported, inaccessible, or outside scan limits.")));
            if ((bool)inspection["truncated"] || ((JArray)inspection["issues"]).Count > 0)
                unresolved.Add(new JObject(new JProperty("reason", "INCOMPLETE_FOLDER_SCAN: narrow the selected folder and resolve inspection issues."), new JProperty("issues", inspection["issues"])));
            return new JObject(new JProperty("version", PlanVersion), new JProperty("document", DocumentIdentity(doc)), new JProperty("root", root),
                new JProperty("items", items), new JProperty("unresolved", unresolved), new JProperty("excluded", excluded),
                new JProperty("ready", unresolved.Count == 0 && items.Count > 0 && items.Count <= 100),
                new JProperty("levels", new JArray(levels.Select(delegate(Level l) { return new JObject(new JProperty("id", l.Id.IntegerValue), new JProperty("name", l.Name), new JProperty("elevationMm", U.ToMm(l.Elevation))); }))),
                new JProperty("views", new JArray(views.Select(delegate(ViewPlan v) { return new JObject(new JProperty("id", v.Id.IntegerValue), new JProperty("name", v.Name), new JProperty("levelId", v.GenLevel != null ? v.GenLevel.Id.IntegerValue : 0)); }))),
                new JProperty("notes", new JArray("Review every floor, unit and anchor before apply. Maximum 100 placements, 256 MiB per file and 1 GiB content per batch.", "DWG/DXF contents, title blocks, Xrefs and paper-space viewports are not parsed. Filename suggestions are not verified geometry.", "Multi-floor sheets require per-floor files or a future region extraction adapter. No automatic cropping/splitting is claimed.", "Selected file identity uses SHA-256. Files must stay unchanged until application completes.", "Placement coordinates refer to the Revit internal coordinate system. No shared-coordinate acquisition is performed.")));
        }

        static bool ValidUnit(string s) { return s == "millimeter" || s == "centimeter" || s == "meter" || s == "inch" || s == "foot"; }
        static ImportUnit Unit(string s)
        {
            switch (s) { case "millimeter": return ImportUnit.Millimeter; case "centimeter": return ImportUnit.Centimeter; case "meter": return ImportUnit.Meter; case "inch": return ImportUnit.Inch; case "foot": return ImportUnit.Foot; default: throw new ArgumentException("Invalid CAD unit."); }
        }
        static JObject Point(double x, double y, double z) { return new JObject(new JProperty("x", x), new JProperty("y", y), new JProperty("z", z)); }
        static void Finite(double n, string name) { if (double.IsNaN(n) || double.IsInfinity(n)) throw new ArgumentException("Non-finite number: " + name); }
        static void ValidatePoint(JObject p, string name)
        {
            if (p == null) throw new ArgumentException("Missing " + name);
            foreach (string axis in new[] { "x", "y", "z" }) Finite(A.NumReq(p, axis), name + "." + axis);
        }

        static Schema MarkerSchema(bool create)
        {
            Schema schema = Schema.Lookup(MarkerGuid);
            if (schema == null && create)
            {
                SchemaBuilder builder = new SchemaBuilder(MarkerGuid);
                builder.SetSchemaName("RevitMcpCadPlacementV1"); builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public);
                builder.AddSimpleField("PlacementKey", typeof(string)); builder.AddSimpleField("SettingsHash", typeof(string));
                builder.AddSimpleField("SourcePath", typeof(string)); builder.AddSimpleField("FileFingerprint", typeof(string));
                builder.AddSimpleField("PlacementTransform", typeof(string)); builder.AddSimpleField("LevelUniqueId", typeof(string)); builder.AddSimpleField("ViewUniqueId", typeof(string));
                schema = builder.Finish();
            }
            return schema;
        }

        static string Hash(string value)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        static string Key(string full, JObject item)
        {
            return Hash(full.ToUpperInvariant() + "|" + A.StrReq(item, "levelName") + "|" +
                (A.Bool(item, "thisViewOnly", true) ? A.StrReq(item, "viewName") : "MODEL"));
        }

        static string Settings(JObject item)
        {
            JObject settings = new JObject();
            foreach (string field in new[] { "unit", "sourceAnchorMm", "targetAnchorMm", "rotationDegrees", "thisViewOnly", "fingerprint", "mode" }) settings[field] = item[field] != null ? item[field].DeepClone() : JValue.CreateNull();
            return Hash(settings.ToString(Formatting.None));
        }

        static JArray TransformValues(Transform t)
        {
            return new JArray(t.Origin.X, t.Origin.Y, t.Origin.Z, t.BasisX.X, t.BasisX.Y, t.BasisX.Z,
                t.BasisY.X, t.BasisY.Y, t.BasisY.Z, t.BasisZ.X, t.BasisZ.Y, t.BasisZ.Z);
        }

        static ImportInstance Existing(Document doc, string full, JObject item, string key, string settings)
        {
            Schema schema = MarkerSchema(false);
            ImportInstance tracked = null;
            foreach (ImportInstance instance in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))
            {
                if (schema != null)
                {
                    Entity entity = instance.GetEntity(schema);
                    if (entity.IsValid() && entity.Get<string>("PlacementKey") == key)
                    {
                        ExternalFileReference actualReference = instance.IsLinked ? ExternalFileUtils.GetExternalFileReference(doc, instance.GetTypeId()) : null;
                        if (actualReference == null || !string.Equals(Path.GetFullPath(ModelPathUtils.ConvertModelPathToUserVisiblePath(actualReference.GetAbsolutePath())), full, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("A tracked CAD link now references a different file. Review it before retrying: " + instance.Id);
                        if (entity.Get<string>("SettingsHash") != settings)
                            throw new InvalidOperationException("The drawing is already placed with different settings or source fingerprint. Review/update that link explicitly: " + full);
                        // Detect common manual changes instead of declaring a moved placement unchanged.
                        Transform transform = instance.GetTotalTransform();
                        JArray recorded = JArray.Parse(entity.Get<string>("PlacementTransform"));
                        JArray currentTransform = TransformValues(transform);
                        if (recorded.Count != currentTransform.Count) throw new InvalidOperationException("Invalid CAD placement tracking data.");
                        for (int value = 0; value < recorded.Count; value++)
                            if (Math.Abs((double)recorded[value] - (double)currentTransform[value]) > (value < 3 ? U.ToFt(1) : 1e-7))
                                throw new InvalidOperationException("A previous CAD placement transform was changed. Review it before retrying: " + instance.Id);
                        Level recordedLevel = doc.GetElement(entity.Get<string>("LevelUniqueId")) as Level;
                        ViewPlan recordedView = doc.GetElement(entity.Get<string>("ViewUniqueId")) as ViewPlan;
                        if (recordedLevel == null || recordedLevel.Name != A.StrReq(item, "levelName") || Math.Abs(U.ToMm(recordedLevel.Elevation) - A.NumReq(item, "elevationMm")) > 0.1 ||
                            recordedView == null || recordedView.Name != A.StrReq(item, "viewName") || recordedView.GenLevel == null || recordedView.GenLevel.Id != recordedLevel.Id ||
                            (A.Bool(item, "thisViewOnly", true) && instance.OwnerViewId != recordedView.Id))
                            throw new InvalidOperationException("A previous CAD destination was changed. Re-plan the layout.");
                        XYZ actual = transform.OfPoint(A.PointFtReq(item, "sourceAnchorMm"));
                        if (actual.DistanceTo(A.PointFtReq(item, "targetAnchorMm")) > U.ToFt(1))
                            throw new InvalidOperationException("A previous CAD placement was moved. Review it before retrying: " + instance.Id);
                        double expected = A.Num(item, "rotationDegrees", 0) * Math.PI / 180.0;
                        XYZ axis = new XYZ(Math.Cos(expected), Math.Sin(expected), 0);
                        if (transform.BasisX.Normalize().DotProduct(axis) < 0.999999)
                            throw new InvalidOperationException("A previous CAD placement was rotated. Review it before retrying: " + instance.Id);
                        if (tracked != null) throw new InvalidOperationException("Multiple CAD instances carry the same placement marker. Resolve duplicates before retrying.");
                        tracked = instance;
                        continue;
                    }
                }
                if (!instance.IsLinked) continue;
                ExternalFileReference reference = ExternalFileUtils.GetExternalFileReference(doc, instance.GetTypeId());
                if (reference == null) continue;
                string existingPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
                if (!string.Equals(Path.GetFullPath(existingPath), full, StringComparison.OrdinalIgnoreCase)) continue;
                ViewPlan ownerView = doc.GetElement(instance.OwnerViewId) as ViewPlan;
                Level ownerLevel = doc.GetElement(instance.LevelId) as Level;
                bool sameDestination = A.Bool(item, "thisViewOnly", true)
                    ? ownerView != null && (ownerView.Id.IntegerValue == A.Int(item, "viewId", 0) ||
                        (ownerView.Name == A.StrReq(item, "viewName") && ownerView.GenLevel != null && ownerView.GenLevel.Name == A.StrReq(item, "levelName")))
                    : !instance.ViewSpecific && (ownerLevel == null || ownerLevel.Id.IntegerValue == A.Int(item, "levelId", 0) || ownerLevel.Name == A.StrReq(item, "levelName"));
                if (sameDestination) throw new InvalidOperationException("An untracked CAD link already exists at this destination. Review it instead of creating a duplicate: " + instance.Id);
            }
            return tracked;
        }

        static void ValidateItem(Document doc, string root, JObject item)
        {
            CadPlanning.ValidateTargetReferences(item);
            string full = CadPlanning.UnderRoot(root, A.StrReq(item, "relativePath"));
            string ext = Path.GetExtension(full);
            if (!ext.Equals(".dwg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".dxf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only DWG/DXF links are supported.");
            if (CadPlanning.Fingerprint(full) != A.StrReq(item, "fingerprint")) throw new InvalidOperationException("CAD file changed since planning: " + full);
            Unit(A.StrReq(item, "unit"));
            if (A.StrReq(item, "mode") != "link") throw new ArgumentException("Only link mode is supported; no silent import fallback.");
            ValidatePoint(A.Obj(item, "sourceAnchorMm"), "sourceAnchorMm"); ValidatePoint(A.Obj(item, "targetAnchorMm"), "targetAnchorMm");
            double elevation = A.NumReq(item, "elevationMm"); Finite(elevation, "elevationMm"); Finite(A.NumReq(item, "rotationDegrees"), "rotationDegrees");
            if (Math.Abs(A.NumReq(A.Obj(item, "targetAnchorMm"), "z") - elevation) > 0.1) throw new ArgumentException("Target Z must equal the target level elevation.");
            if (string.IsNullOrWhiteSpace(A.StrReq(item, "levelName")) || string.IsNullOrWhiteSpace(A.StrReq(item, "viewName"))) throw new ArgumentException("Empty level/view names are not allowed.");
            Level level = doc.GetElement(new ElementId(A.IntReq(item, "levelId"))) as Level;
            if (!A.Bool(item, "createLevel", false))
            {
                if (level == null || level.Name != A.StrReq(item, "levelName") || Math.Abs(U.ToMm(level.Elevation) - elevation) > 0.1)
                    throw new InvalidOperationException("Target level changed or disappeared. Re-plan the CAD layout.");
            }
            else
            {
                List<Level> matching = Levels(doc).Where(delegate(Level l) { return l.Name == A.StrReq(item, "levelName"); }).ToList();
                if (matching.Count > 1 || (matching.Count == 1 && Math.Abs(U.ToMm(matching[0].Elevation) - elevation) > 0.1))
                    throw new InvalidOperationException("New-level name now conflicts with a different elevation.");
            }
            if (!A.Bool(item, "createView", false))
            {
                ViewPlan view = doc.GetElement(new ElementId(A.IntReq(item, "viewId"))) as ViewPlan;
                if (view == null || view.IsTemplate || view.Name != A.StrReq(item, "viewName") || view.GenLevel == null || level == null || view.GenLevel.Id != level.Id ||
                    (view.ViewType != ViewType.FloorPlan && view.ViewType != ViewType.EngineeringPlan)) throw new InvalidOperationException("Target plan view changed. Re-plan the CAD layout.");
            }
        }

        static object Apply(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp); JObject plan = A.Obj(args, "plan");
            if (plan == null || A.Str(plan, "version", null) != PlanVersion) throw new ArgumentException("A complete plan_cad_layout result is required.");
            if (!JToken.DeepEquals(plan["document"], DocumentIdentity(doc))) throw new InvalidOperationException("The CAD plan belongs to a different document. Re-plan in the intended project.");
            if (doc.IsFamilyDocument || doc.IsReadOnly) throw new InvalidOperationException("A writable project document is required.");
            JArray unresolved = A.Arr(plan, "unresolved"); JArray items = A.Arr(plan, "items");
            if (unresolved == null || unresolved.Count != 0 || items == null || items.Count == 0 || items.Count > 100) throw new ArgumentException("Resolve every plan issue and use 1..100 placements per batch.");
            string root = CadPlanning.LocalPath(A.StrReq(plan, "root"));
            JArray results = new JArray(); HashSet<string> keys = new HashSet<string>();
            long batchBytes = 0;
            Dictionary<string, double> newLevels = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken token in items)
            {
                JObject item = token as JObject; if (item == null) throw new ArgumentException("Invalid placement item.");
                string full = CadPlanning.UnderRoot(root, A.StrReq(item, "relativePath"));
                batchBytes += new FileInfo(full).Length;
                if (batchBytes > CadPlanning.MaxBatchBytes) throw new ArgumentException("Select at most 1 GiB of drawing content per batch.");
                ValidateItem(doc, root, item);
                string name = A.StrReq(item, "levelName"); double elevation = A.NumReq(item, "elevationMm");
                if (newLevels.ContainsKey(name) && Math.Abs(newLevels[name] - elevation) > 0.1) throw new ArgumentException("Conflicting elevations for the same level name.");
                newLevels[name] = elevation;
                string key = Key(full, item);
                if (!keys.Add(key)) throw new ArgumentException("Duplicate source/destination placement in this plan.");
                ImportInstance existing = Existing(doc, full, item, key, Settings(item));
                results.Add(new JObject(new JProperty("relativePath", item["relativePath"]), new JProperty("action", existing != null ? "alreadyPresent" : "link"), new JProperty("elementId", existing != null ? existing.Id.IntegerValue : 0)));
            }
            if (A.Bool(args, "dryRun", true)) return new JObject(new JProperty("dryRun", true), new JProperty("validated", true), new JProperty("items", results), new JProperty("modelChanged", false));
            return Rx.Tx(doc, "MCP: batch CAD floor layout", delegate
            {
                    for (int i = 0; i < items.Count; i++)
                    {
                        JObject item = (JObject)items[i]; JObject result = (JObject)results[i];
                        if ((string)result["action"] == "alreadyPresent") continue;
                        string full = CadPlanning.UnderRoot(root, A.StrReq(item, "relativePath"));
                        if (CadPlanning.Fingerprint(full) != A.StrReq(item, "fingerprint")) throw new InvalidOperationException("CAD file changed during application.");
                        Level level = doc.GetElement(new ElementId(A.Int(item, "levelId", 0))) as Level;
                        if (level == null)
                        {
                            level = Levels(doc).FirstOrDefault(delegate(Level l) { return l.Name == (string)item["levelName"]; });
                            if (level == null) { level = Level.Create(doc, U.ToFt(A.NumReq(item, "elevationMm"))); level.Name = A.StrReq(item, "levelName"); }
                        }
                        ViewPlan view = doc.GetElement(new ElementId(A.Int(item, "viewId", 0))) as ViewPlan;
                        if (view == null)
                        {
                            view = Views(doc).FirstOrDefault(delegate(ViewPlan v) { return v.GenLevel != null && v.GenLevel.Id == level.Id && v.Name == (string)item["viewName"]; });
                            if (view == null)
                            {
                                ViewFamilyType type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(delegate(ViewFamilyType t) { return t.ViewFamily == ViewFamily.FloorPlan; });
                                if (type == null) throw new InvalidOperationException("No floor plan ViewFamilyType exists in this project.");
                                view = ViewPlan.Create(doc, type.Id, level.Id); view.Name = A.StrReq(item, "viewName");
                            }
                        }
                        ElementId id;
                        using (DWGImportOptions options = new DWGImportOptions())
                        {
                            options.Unit = Unit(A.StrReq(item, "unit")); options.Placement = ImportPlacement.Origin;
                            options.ThisViewOnly = A.Bool(item, "thisViewOnly", true); options.OrientToView = false;
                            options.ColorMode = ImportColorMode.Preserved; options.VisibleLayersOnly = false;
                            if (!doc.Link(full, options, view, out id)) throw new InvalidOperationException("Revit rejected CAD link: " + full);
                        }
                        if (CadPlanning.Fingerprint(full) != A.StrReq(item, "fingerprint")) throw new InvalidOperationException("CAD content changed while Revit was linking; the batch will roll back.");
                        ImportInstance instance = doc.GetElement(id) as ImportInstance;
                        if (instance == null || !instance.IsLinked) throw new InvalidOperationException("CAD link did not return a linked ImportInstance.");
                        doc.Regenerate();
                        bool pinned = instance.Pinned; if (pinned) instance.Pinned = false;
                        XYZ source = A.PointFtReq(item, "sourceAnchorMm"), target = A.PointFtReq(item, "targetAnchorMm");
                        double desired = A.NumReq(item, "rotationDegrees") * Math.PI / 180.0;
                        Transform initial = instance.GetTotalTransform();
                        double current = Math.Atan2(initial.BasisX.Y, initial.BasisX.X);
                        XYZ pivot = initial.OfPoint(source);
                        if (Math.Abs(desired - current) > 1e-10) ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(pivot, pivot + XYZ.BasisZ), desired - current);
                        doc.Regenerate();
                        XYZ shift = target - instance.GetTotalTransform().OfPoint(source);
                        if (shift.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, id, shift);
                        doc.Regenerate();
                        if (instance.GetTotalTransform().OfPoint(source).DistanceTo(target) > U.ToFt(1)) throw new InvalidOperationException("CAD anchor verification failed; the batch will roll back.");
                        instance.Pinned = true;
                        Schema schema = MarkerSchema(true); Entity marker = new Entity(schema);
                        marker.Set<string>("PlacementKey", Key(full, item)); marker.Set<string>("SettingsHash", Settings(item));
                        marker.Set<string>("SourcePath", full); marker.Set<string>("FileFingerprint", A.StrReq(item, "fingerprint"));
                        marker.Set<string>("PlacementTransform", TransformValues(instance.GetTotalTransform()).ToString(Formatting.None));
                        marker.Set<string>("LevelUniqueId", level.UniqueId); marker.Set<string>("ViewUniqueId", view.UniqueId); instance.SetEntity(marker);
                        result["elementId"] = id.IntegerValue; result["viewId"] = view.Id.IntegerValue; result["levelId"] = level.Id.IntegerValue; result["action"] = "linked";
                    }
                return new JObject(new JProperty("dryRun", false), new JProperty("items", results), new JProperty("committed", true), new JProperty("saved", false), new JProperty("synchronized", false));
            });
        }
    }
}
