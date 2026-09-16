using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    internal static class ClashTools
    {
        const double Mm = 304.8;
        static readonly Dictionary<string, ScanRecord> Scans = new Dictionary<string, ScanRecord>();

        sealed class ElementRef
        {
            public string HostModel, SourceModel, UniqueId, LinkUniqueId;
            public long Id, LinkId;
            public string Identity { get { return HostModel + "/" + LinkUniqueId + "/" + SourceModel + "/" + UniqueId; } }
        }
        sealed class Candidate
        {
            public Element Element; public RevitLinkInstance Link; public Transform Transform;
            public ElementRef Ref; public ClashBounds Box;
        }
        sealed class Issue
        {
            public string Id; public ElementRef A, B; public ClashBounds Box;
            public double MaxIntersectionMm3; public int IntersectionCount;
            public JObject Json;
        }
        sealed class ScanRecord
        {
            public string Id; public Document Document; public DateTime Created;
            public double Threshold; public ElementId PhaseId;
            public readonly Dictionary<string, Issue> Issues = new Dictionary<string, Issue>();
        }
        sealed class Budget
        {
            public readonly Stopwatch Watch = Stopwatch.StartNew();
            public int Milliseconds, MaxPairs, MaxBooleans, Pairs, Booleans;
            public string StopReason;
            public void Time()
            {
                if (Watch.ElapsedMilliseconds >= Milliseconds)
                { StopReason = "timeBudgetMs"; throw new LimitReached(); }
            }
            public void Pair()
            {
                Time(); if (Pairs >= MaxPairs) { StopReason = "maxPairs"; throw new LimitReached(); } Pairs++;
            }
            public void Boolean()
            {
                Time(); if (Booleans >= MaxBooleans) { StopReason = "maxBooleanOperations"; throw new LimitReached(); } Booleans++;
            }
        }
        sealed class LimitReached : Exception { }
        sealed class SolidSet : IDisposable
        {
            public readonly List<Solid> Solids = new List<Solid>();
            public string Error;
            public void Dispose() { foreach (Solid solid in Solids) solid.Dispose(); Solids.Clear(); }
        }

        public static void Register()
        {
            S reference = S.Obj().IntReq("elementId", "Element id (64-bit integer).")
                .Int("linkInstanceId", "Optional host Revit link instance id; omitted means host element.");
            S group = S.Obj().ObjArr("elements", "Explicit host/link element references. Use either elements or categories.", reference, false)
                .StrArr("categories", "Built-in or display category names. Use either categories or elements.", false)
                .Bool("includeHost", "Include host document for category selection.", true)
                .IntArr("linkInstanceIds", "Explicit loaded, direct Revit link instances to include in category selection.", false);
            ToolRegistry.Register("scan_clashes",
                "Bounded solid-volume clash scan of explicit source/target scopes. Returns host/link identities, partial results and omissions. Does not modify the model. Direct connections are classified separately; clearance and nested links are not checked.",
                S.Obj().Sub("sources", "Source scope", group, true).Sub("targets", "Target scope", group, true)
                    .NumDef("minimumIntersectionMm3", "Each individual solid-pair intersection must exceed this threshold; not a project acceptance tolerance.", 1)
                    .Int("phaseId", "Host phase id. Defaults to active view phase. Linked phase mapping is required.")
                    .IntDef("maxElementsPerGroup", "1..1000; overflow is reported as partial coverage.", 200)
                    .IntDef("maxPairs", "1..50000 bounding-box pair comparisons.", 10000)
                    .IntDef("maxBooleanOperations", "1..5000 exact solid intersections.", 1000)
                    .IntDef("maxIssues", "1..500 findings.", 100)
                    .IntDef("timeBudgetMs", "100..5000 cooperative budget. A single Revit geometry operation cannot be interrupted.", 2000), Scan);
            ToolRegistry.Register("create_clash_review_views",
                "Recheck issues from a recent scan and create unique section-box 3D review views. Defaults to dryRun. Never overwrites existing views or saves/synchronizes. Host elements are colored; linked results highlight the whole link instance and explicitly report that limitation.",
                S.Obj().StrReq("scanId", "Session scan identifier from scan_clashes; expires after 30 minutes.")
                    .StrArr("issueIds", "1..20 issue ids from that scan.", true)
                    .Enum("mode", "One view per issue or one grouped view.", new string[] { "perIssue", "grouped" }, "perIssue")
                    .StrDef("namePrefix", "New view name prefix.", "MCP Clash")
                    .NumDef("paddingMm", "50..10000 around verified intersection region.", 500)
                    .Bool("dryRun", "Preview geometry recheck and proposed views without a transaction.", true), CreateViews);
        }

        static int Integer(JObject args, string key, int def, int min, int max)
        {
            JToken t = args[key];
            if (t != null && t.Type != JTokenType.Integer) throw new ArgumentException(key + " must be an integer.");
            int value = t == null ? def : t.Value<int>();
            if (value < min || value > max) throw new ArgumentException(key + " must be " + min + ".." + max + ".");
            return value;
        }
        static double Number(JObject args, string key, double def, double min, double max)
        {
            JToken t = args[key];
            if (t != null && t.Type != JTokenType.Float && t.Type != JTokenType.Integer) throw new ArgumentException(key + " must be numeric.");
            double value = t == null ? def : t.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
                throw new ArgumentException(key + " is outside the supported range.");
            return value;
        }
        static long Id(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer || token.Value<long>() <= 0)
                throw new ArgumentException("Element ids must be positive 64-bit integers.");
            return token.Value<long>();
        }
        static string Model(Document doc) { return doc.ProjectInformation.UniqueId; }
        static ElementId Phase(Document doc, JObject args)
        {
            if (args["phaseId"] != null)
            {
                ElementId id = new ElementId(Id(args["phaseId"]));
                if (!(doc.GetElement(id) is Phase)) throw new ArgumentException("phaseId is not a phase in the host document.");
                return id;
            }
            Parameter p = doc.ActiveView == null ? null : doc.ActiveView.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if (p == null || p.AsElementId() == ElementId.InvalidElementId)
                throw new ArgumentException("The active view has no phase; provide an explicit phaseId.");
            return p.AsElementId();
        }
        static void Notice(JArray notices, string scope, string state, string message, ElementRef reference)
        {
            JObject n = new JObject(new JProperty("scope", scope), new JProperty("state", state), new JProperty("message", message));
            if (reference != null) n["element"] = RefJson(reference);
            notices.Add(n);
        }
        static ElementRef Reference(Document host, Element element, RevitLinkInstance link)
        {
            return new ElementRef { HostModel = Model(host), SourceModel = Model(element.Document), UniqueId = element.UniqueId,
                Id = element.Id.Value, LinkId = link == null ? 0 : link.Id.Value, LinkUniqueId = link == null ? "" : link.UniqueId };
        }
        static JObject RefJson(ElementRef r)
        {
            return new JObject(new JProperty("hostModel", r.HostModel), new JProperty("sourceModel", r.SourceModel),
                new JProperty("elementId", r.Id), new JProperty("uniqueId", r.UniqueId),
                new JProperty("linkInstanceId", r.LinkId == 0 ? (object)null : r.LinkId), new JProperty("linkUniqueId", r.LinkUniqueId));
        }
        static Candidate Resolve(Document host, ElementRef r)
        {
            if (Model(host) != r.HostModel) throw new InvalidOperationException("The issue belongs to another host document.");
            RevitLinkInstance link = string.IsNullOrEmpty(r.LinkUniqueId) ? null : host.GetElement(r.LinkUniqueId) as RevitLinkInstance;
            Document source = string.IsNullOrEmpty(r.LinkUniqueId) ? host : (link == null ? null : link.GetLinkDocument());
            if (source == null || Model(source) != r.SourceModel) throw new InvalidOperationException("The link is unloaded, deleted or replaced.");
            Element element = source.GetElement(r.UniqueId);
            if (element == null) throw new InvalidOperationException("The issue element is missing or was recreated.");
            return CandidateFor(host, element, link);
        }
        static Candidate CandidateFor(Document host, Element element, RevitLinkInstance link)
        {
            if (element == null) throw new ArgumentException("Element was not found.");
            if (element.Category == null || element.ViewSpecific || element is ElementType || element is RevitLinkInstance)
                throw new ArgumentException("Element has no supported model geometry (nested links are not scanned).");
            DirectShape ds = element as DirectShape;
            if (ds != null && (ds.ApplicationId ?? "").StartsWith("Codex.RoomOverlapReview", StringComparison.Ordinal))
                throw new ArgumentException("Review-only geometry is excluded.");
            Transform transform = link == null ? Transform.Identity : link.GetTotalTransform();
            BoundingBoxXYZ box = element.get_BoundingBox(null);
            if (box == null) throw new ArgumentException("No bounding box; element is unverified.");
            return new Candidate { Element = element, Link = link, Transform = transform,
                Ref = Reference(host, element, link), Box = Bounds(box, transform) };
        }
        static ClashBounds Bounds(BoundingBoxXYZ box, Transform outer)
        {
            double[] min = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            double[] max = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            for (int i = 0; i < 8; i++)
            {
                XYZ p = outer.OfPoint(box.Transform.OfPoint(new XYZ((i & 1) == 0 ? box.Min.X : box.Max.X,
                    (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z)));
                double[] xyz = { p.X, p.Y, p.Z };
                for (int k = 0; k < 3; k++) { min[k] = Math.Min(min[k], xyz[k]); max[k] = Math.Max(max[k], xyz[k]); }
            }
            return new ClashBounds(min, max);
        }
        static JObject BoundsJson(ClashBounds box)
        {
            return new JObject(new JProperty("min", Rx.PtJson(new XYZ(box.Min[0], box.Min[1], box.Min[2]))),
                new JProperty("max", Rx.PtJson(new XYZ(box.Max[0], box.Max[1], box.Max[2]))));
        }
        static bool InPhase(Candidate c, ElementId hostPhase)
        {
            if (c.Element.DesignOption != null && !c.Element.DesignOption.IsPrimary)
                throw new InvalidOperationException("Non-primary design option is excluded; compare options in separate scopes.");
            if (!c.Element.HasPhases()) return true;
            ElementId phase = hostPhase;
            if (c.Link != null)
            {
                RevitLinkType type = c.Link.Document.GetElement(c.Link.GetTypeId()) as RevitLinkType;
                IDictionary<ElementId, ElementId> map = type.GetPhaseMap();
                if (!map.TryGetValue(hostPhase, out phase)) throw new InvalidOperationException("No linked phase mapping; element is unverified.");
            }
            ElementOnPhaseStatus status = c.Element.GetPhaseStatus(phase);
            return status == ElementOnPhaseStatus.New || status == ElementOnPhaseStatus.Existing || status == ElementOnPhaseStatus.Temporary;
        }

        static List<Candidate> Collect(Document host, JObject group, string label, int max, ElementId phase, Budget budget, JArray notices)
        {
            if (group == null) throw new ArgumentException(label + " is required.");
            JArray elements = group["elements"] as JArray, categories = group["categories"] as JArray;
            if ((elements != null && elements.Count > 0) == (categories != null && categories.Count > 0))
                throw new ArgumentException(label + " must contain exactly one nonempty elements or categories array.");
            List<Candidate> result = new List<Candidate>(); HashSet<string> seen = new HashSet<string>();
            Action<Element, RevitLinkInstance> add = delegate(Element element, RevitLinkInstance link)
            {
                budget.Time(); ElementRef reference = element == null ? null : Reference(host, element, link);
                try
                {
                    Candidate c = CandidateFor(host, element, link);
                    if (!seen.Add(c.Ref.Identity)) return;
                    if (!InPhase(c, phase)) { Notice(notices, label, "excludedPhase", "Not present in the selected phase.", c.Ref); return; }
                    result.Add(c);
                }
                catch (LimitReached) { throw; }
                catch (Exception ex) { Notice(notices, label, "unverified", ex.Message, reference); }
            };
            if (elements != null && elements.Count > 0)
            {
                if (elements.Count > max) throw new ArgumentException(label + " explicit references exceed maxElementsPerGroup; split the input.");
                foreach (JToken item in elements)
                {
                    budget.Time(); JObject e = item as JObject;
                    if (e == null) throw new ArgumentException("Each element reference must be an object.");
                    long id = Id(e["elementId"]);
                    RevitLinkInstance link = e["linkInstanceId"] == null ? null : host.GetElement(new ElementId(Id(e["linkInstanceId"]))) as RevitLinkInstance;
                    if (e["linkInstanceId"] != null && (link == null || link.GetLinkDocument() == null))
                    { Notice(notices, label, "unverified", "Requested link is missing/unloaded: " + e["linkInstanceId"], null); continue; }
                    Document source = link == null ? host : link.GetLinkDocument();
                    Element found = source.GetElement(new ElementId(id));
                    if (found == null)
                    {
                        Notice(notices, label, "unverified", "Requested element is missing: " + id, null);
                        ((JObject)notices[notices.Count - 1])["requestedElement"] = e.DeepClone();
                    }
                    else add(found, link);
                }
                return result;
            }
            List<BuiltInCategory> cats = new List<BuiltInCategory>();
            foreach (JToken item in categories)
            {
                BuiltInCategory cat = Rx.ResolveCategory(host, item.ToString());
                if (cat == BuiltInCategory.INVALID) throw new ArgumentException("Unknown category: " + item);
                cats.Add(cat);
            }
            List<RevitLinkInstance> links = new List<RevitLinkInstance>();
            if (A.Bool(group, "includeHost", true)) links.Add(null);
            JArray linkIds = group["linkInstanceIds"] as JArray;
            if (linkIds != null)
            {
                if (linkIds.Count > 20) throw new ArgumentException("At most 20 explicit link instances per group are supported.");
                foreach (JToken idToken in linkIds)
                {
                    budget.Time(); RevitLinkInstance link = host.GetElement(new ElementId(Id(idToken))) as RevitLinkInstance;
                    if (link == null || link.GetLinkDocument() == null) Notice(notices, label, "unverified", "Requested link is missing/unloaded: " + idToken, null);
                    else if (!links.Contains(link)) links.Add(link);
                }
            }
            if (links.Count == 0) throw new ArgumentException(label + " selects no available document.");
            int visited = 0;
            foreach (RevitLinkInstance link in links)
            {
                budget.Time(); Document source = link == null ? host : link.GetLinkDocument();
                using (FilteredElementCollector collector = new FilteredElementCollector(source))
                {
                    collector.WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(cats));
                    foreach (Element element in collector)
                    {
                        budget.Time();
                        if (visited++ >= max) { Notice(notices, label, "limit", "maxElementsPerGroup reached; narrow the scope. Remaining elements were not enumerated.", null); return result; }
                        add(element, link);
                    }
                }
            }
            return result;
        }

        static SolidSet Solids(Candidate candidate, Budget budget)
        {
            SolidSet result = new SolidSet();
            try
            {
                using (Options options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false, ComputeReferences = false })
                using (GeometryElement geometry = candidate.Element.get_Geometry(options))
                    Unpack(geometry, candidate.Transform, result, budget, 0);
                if (result.Solids.Count == 0) result.Error = "No supported positive-volume solids; mesh/line-only geometry is unverified.";
            }
            catch (LimitReached) { result.Dispose(); throw; }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }
        static void Unpack(GeometryElement geometry, Transform transform, SolidSet result, Budget budget, int depth)
        {
            if (geometry == null) return;
            if (depth > 12) throw new InvalidOperationException("Geometry nesting limit reached; element is unverified.");
            foreach (GeometryObject obj in geometry)
            {
                budget.Time(); Solid solid = obj as Solid;
                if (solid != null && solid.Faces.Size > 0 && solid.Volume > 1e-12)
                {
                    if (result.Solids.Count >= 100) throw new InvalidOperationException("100 solids per element limit reached; element is unverified.");
                    result.Solids.Add(SolidUtils.CreateTransformed(solid, transform));
                }
                GeometryInstance instance = obj as GeometryInstance;
                if (instance != null)
                    using (GeometryElement nested = instance.GetInstanceGeometry()) Unpack(nested, transform, result, budget, depth + 1);
            }
        }
        static ConnectorManager Connectors(Element e)
        {
            MEPCurve curve = e as MEPCurve;
            if (curve != null) return curve.ConnectorManager;
            FamilyInstance family = e as FamilyInstance;
            return family == null || family.MEPModel == null ? null : family.MEPModel.ConnectorManager;
        }
        static bool Connected(Candidate a, Candidate b)
        {
            if (a.Ref.SourceModel != b.Ref.SourceModel || a.Ref.LinkUniqueId != b.Ref.LinkUniqueId) return false;
            InsulationLiningBase ia = a.Element as InsulationLiningBase, ib = b.Element as InsulationLiningBase;
            if ((ia != null && ia.HostElementId == b.Element.Id) || (ib != null && ib.HostElementId == a.Element.Id)) return true;
            FamilyInstance fa = a.Element as FamilyInstance, fb = b.Element as FamilyInstance;
            if ((fa != null && fa.SuperComponent != null && fa.SuperComponent.Id == b.Element.Id) ||
                (fb != null && fb.SuperComponent != null && fb.SuperComponent.Id == a.Element.Id)) return true;
            ConnectorManager manager = Connectors(a.Element);
            if (manager == null) return false;
            foreach (Connector connector in manager.Connectors)
                if (connector.ConnectorType == ConnectorType.End)
                    foreach (Connector other in connector.AllRefs)
                        if (other.ConnectorType == ConnectorType.End && other.Owner.Id == b.Element.Id) return true;
            return false;
        }
        static Issue Check(Candidate a, Candidate b, SolidSet sa, SolidSet sb, double threshold, Budget budget)
        {
            if (sa.Error != null || sb.Error != null) throw new InvalidOperationException((sa.Error == null ? "" : "A: " + sa.Error) + (sb.Error == null ? "" : " B: " + sb.Error));
            double max = 0; int count = 0; ClashBounds overlap = null;
            foreach (Solid s in sa.Solids) foreach (Solid t in sb.Solids)
            {
                budget.Boolean();
                using (Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(s, t, BooleanOperationsType.Intersect))
                {
                    if (intersection == null) throw new InvalidOperationException("Boolean intersection returned no result.");
                    double volume = intersection.Volume * Mm * Mm * Mm;
                    if (volume <= threshold) continue;
                    count++; max = Math.Max(max, volume);
                    ClashBounds box = Bounds(intersection.GetBoundingBox(), Transform.Identity);
                    overlap = overlap == null ? box : overlap.Union(box);
                }
            }
            if (count == 0) return null;
            Issue issue = new Issue { A = a.Ref, B = b.Ref, Id = ClashIdentity.Pair(a.Ref.Identity, b.Ref.Identity),
                Box = overlap, MaxIntersectionMm3 = max, IntersectionCount = count };
            issue.Json = new JObject(new JProperty("issueId", issue.Id), new JProperty("state", "overlap"),
                new JProperty("a", RefJson(a.Ref)), new JProperty("b", RefJson(b.Ref)),
                new JProperty("aName", a.Element.Name), new JProperty("bName", b.Element.Name),
                new JProperty("largestSolidIntersectionMm3", Math.Round(max, 3)), new JProperty("solidIntersectionsAboveThreshold", count),
                new JProperty("boundsMm", BoundsJson(overlap)), new JProperty("reviewRequired", true));
            return issue;
        }

        static object Scan(UIApplication app, JObject args)
        {
            Document doc = Rx.Doc(app);
            if (doc.IsFamilyDocument) throw new InvalidOperationException("Clash scans require a project document.");
            Budget budget = new Budget { Milliseconds = Integer(args, "timeBudgetMs", 2000, 100, 5000),
                MaxPairs = Integer(args, "maxPairs", 10000, 1, 50000), MaxBooleans = Integer(args, "maxBooleanOperations", 1000, 1, 5000) };
            int maxElements = Integer(args, "maxElementsPerGroup", 200, 1, 1000), maxIssues = Integer(args, "maxIssues", 100, 1, 500);
            ScanRecord record = new ScanRecord { Document = doc, Id = Guid.NewGuid().ToString("N"), Created = DateTime.UtcNow,
                Threshold = Number(args, "minimumIntersectionMm3", 1, 0.000001, 1e15), PhaseId = Phase(doc, args) };
            List<Candidate> sources = new List<Candidate>(), targets = new List<Candidate>(); JArray notices = new JArray(), issues = new JArray();
            Dictionary<string, SolidSet> solids = new Dictionary<string, SolidSet>(); HashSet<string> seen = new HashSet<string>();
            int broadCandidates = 0, clear = 0, connections = 0, errors = 0, rejectedByBox = 0; bool finished = false;
            try
            {
                sources = Collect(doc, A.Obj(args, "sources"), "sources", maxElements, record.PhaseId, budget, notices);
                targets = Collect(doc, A.Obj(args, "targets"), "targets", maxElements, record.PhaseId, budget, notices);
                foreach (Candidate a in sources) foreach (Candidate b in targets)
                {
                    budget.Time();
                    if (notices.Count >= 1000) { budget.StopReason = "1000 notices"; throw new LimitReached(); }
                    if (a.Ref.Identity == b.Ref.Identity) continue;
                    string pair = ClashIdentity.Pair(a.Ref.Identity, b.Ref.Identity); if (!seen.Add(pair)) continue;
                    budget.Pair();
                    if (!a.Box.Intersects(b.Box)) { rejectedByBox++; continue; }
                    broadCandidates++;
                    try
                    {
                        if (Connected(a, b)) { connections++; continue; }
                        SolidSet sa, sb;
                        if (!solids.TryGetValue(a.Ref.Identity, out sa)) { sa = Solids(a, budget); solids.Add(a.Ref.Identity, sa); }
                        if (!solids.TryGetValue(b.Ref.Identity, out sb)) { sb = Solids(b, budget); solids.Add(b.Ref.Identity, sb); }
                        Issue issue = Check(a, b, sa, sb, record.Threshold, budget);
                        if (issue == null) clear++;
                        else
                        {
                            record.Issues.Add(issue.Id, issue); issues.Add(issue.Json);
                            if (issues.Count >= maxIssues) { budget.StopReason = "maxIssues"; throw new LimitReached(); }
                        }
                    }
                    catch (LimitReached) { throw; }
                    catch (Exception ex)
                    {
                        errors++; Notice(notices, "pair", "unverified", ex.Message, null);
                        JObject notice = (JObject)notices[notices.Count - 1]; notice["a"] = RefJson(a.Ref); notice["b"] = RefJson(b.Ref);
                    }
                }
                finished = true;
            }
            catch (LimitReached) { Notice(notices, "scan", "limit", budget.StopReason + " reached; narrow scopes and rerun. Remaining work is unverified.", null); }
            finally { foreach (SolidSet set in solids.Values) set.Dispose(); }
            foreach (string old in Scans.Where(x => DateTime.UtcNow - x.Value.Created > TimeSpan.FromMinutes(30)).Select(x => x.Key).ToList()) Scans.Remove(old);
            while (Scans.Count >= 8) Scans.Remove(Scans.OrderBy(x => x.Value.Created).First().Key);
            Scans.Add(record.Id, record);
            bool complete = finished && !notices.Any(n => (string)n["state"] != "excludedPhase");
            return new JObject(new JProperty("scanId", record.Id), new JProperty("createdUtc", record.Created.ToString("o")),
                new JProperty("status", complete ? "completed" : "partial"), new JProperty("coverageComplete", complete),
                new JProperty("stopReason", budget.StopReason), new JProperty("elapsedMs", budget.Watch.ElapsedMilliseconds),
                new JProperty("hostModel", Model(doc)), new JProperty("phaseId", record.PhaseId.Value),
                new JProperty("sourceCount", sources.Count), new JProperty("targetCount", targets.Count),
                new JProperty("pairsCompared", budget.Pairs), new JProperty("rejectedByBoundingBox", rejectedByBox),
                new JProperty("boundingBoxCandidates", broadCandidates), new JProperty("exactClearPairs", clear),
                new JProperty("directConnectionPairsExcluded", connections), new JProperty("unverifiedPairs", errors),
                new JProperty("booleanOperations", budget.Booleans), new JProperty("minimumIntersectionMm3", record.Threshold),
                new JProperty("issues", issues), new JProperty("notices", notices),
                new JProperty("scopeLimitations", new JArray("Directly selected host/link instances only; nested links excluded.",
                    "Physical solid overlap only; clearance, maintenance access and opening adequacy are not checked.",
                    "Threshold applies to each individual solid intersection; volumes are not summed to avoid double counting.",
                    "Direct connections/insulation host pairs are excluded and not certified clear.",
                    "Category scans include the selected phase and primary design options; independent of view visibility.",
                    "A single native Revit geometry operation cannot be interrupted by the cooperative time budget.")));
        }

        static object CreateViews(UIApplication app, JObject args)
        {
            Document doc = Rx.Doc(app); ScanRecord scan;
            if (!Scans.TryGetValue(A.StrReq(args, "scanId"), out scan) || DateTime.UtcNow - scan.Created > TimeSpan.FromMinutes(30))
                throw new ArgumentException("Scan is absent/expired. Run scan_clashes again.");
            if (!object.ReferenceEquals(scan.Document, doc)) throw new InvalidOperationException("The scan belongs to another document session.");
            JArray ids = args["issueIds"] as JArray;
            if (ids == null || ids.Count < 1 || ids.Count > 20) throw new ArgumentException("issueIds must contain 1..20 issue ids.");
            string mode = A.Str(args, "mode", "perIssue");
            if (mode != "perIssue" && mode != "grouped") throw new ArgumentException("mode must be perIssue or grouped.");
            double padding = Number(args, "paddingMm", 500, 50, 10000) / Mm;
            bool dryRun = A.Bool(args, "dryRun", true);
            Budget budget = new Budget { Milliseconds = 5000, MaxPairs = 20, MaxBooleans = 5000 };
            List<Issue> fresh = new List<Issue>(); JArray skipped = new JArray(); HashSet<string> duplicate = new HashSet<string>();
            foreach (JToken token in ids)
            {
                string id = token.ToString(); Issue old;
                if (!duplicate.Add(id)) continue;
                if (!scan.Issues.TryGetValue(id, out old)) throw new ArgumentException("Unknown issueId: " + id);
                try
                {
                    budget.Time(); Candidate a = Resolve(doc, old.A), b = Resolve(doc, old.B);
                    if (!InPhase(a, scan.PhaseId) || !InPhase(b, scan.PhaseId)) throw new InvalidOperationException("Issue no longer exists in the selected phase.");
                    if (!a.Box.Intersects(b.Box) || Connected(a, b)) throw new InvalidOperationException("Issue no longer overlaps or is a direct connection; rescan.");
                    using (SolidSet sa = Solids(a, budget)) using (SolidSet sb = Solids(b, budget))
                    {
                        Issue issue = Check(a, b, sa, sb, scan.Threshold, budget);
                        if (issue == null) throw new InvalidOperationException("Issue is no longer above the intersection threshold; rescan.");
                        fresh.Add(issue);
                    }
                }
                catch (LimitReached)
                {
                    skipped.Add(new JObject(new JProperty("issueId", id), new JProperty("reason", "Recheck budget exceeded; no views created. Narrow issueIds.")));
                    break;
                }
                catch (Exception ex) { skipped.Add(new JObject(new JProperty("issueId", id), new JProperty("reason", ex.Message))); }
            }
            // Fail closed: never apply a smaller, unannounced set after stale/unsupported results.
            if (skipped.Count > 0) return new JObject(new JProperty("status", "notApplied"), new JProperty("dryRun", dryRun),
                new JProperty("createdViews", new JArray()), new JProperty("skipped", skipped), new JProperty("message", "All requested issues must pass a current geometry recheck. No transaction was started."));
            HashSet<string> names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
            List<List<Issue>> groups = mode == "grouped" ? new List<List<Issue>> { fresh } : fresh.Select(i => new List<Issue> { i }).ToList();
            List<Tuple<string, List<Issue>, ClashBounds>> plans = new List<Tuple<string, List<Issue>, ClashBounds>>(); JArray proposed = new JArray();
            foreach (List<Issue> group in groups)
            {
                ClashBounds box = group[0].Box; foreach (Issue issue in group.Skip(1)) box = box.Union(issue.Box);
                box = box.Expand(padding);
                string suffix = scan.Id.Substring(0, 8) + "_" + (mode == "grouped" ? "group" : group[0].Id.Substring(0, 10));
                string name = ClashIdentity.UniqueViewName(A.Str(args, "namePrefix", "MCP Clash"), suffix, names);
                plans.Add(Tuple.Create(name, group, box));
                proposed.Add(new JObject(new JProperty("name", name), new JProperty("issueIds", new JArray(group.Select(i => i.Id))),
                    new JProperty("sectionBoxMm", BoundsJson(box)), new JProperty("linkedHighlight", "Whole link instance only; linked subelements are not individually overridden.")));
            }
            if (dryRun) return new JObject(new JProperty("status", "preview"), new JProperty("dryRun", true), new JProperty("plannedViews", proposed));
            if (doc.IsReadOnly || doc.IsFamilyDocument) throw new InvalidOperationException("Writable project document required.");
            ViewFamilyType family = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            if (family == null) throw new InvalidOperationException("No 3D view family type is available.");
            FillPatternElement fill = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            JArray created = new JArray(), notices = new JArray();
            using (Transaction tx = new Transaction(doc, "MCP clash review 3D views"))
            {
                if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start a view creation transaction.");
                FailureHandlingOptions options = tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new RollbackErrors()).SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(options);
                try
                {
                    foreach (Tuple<string, List<Issue>, ClashBounds> plan in plans)
                    {
                        View3D view = View3D.CreateIsometric(doc, family.Id); view.ViewTemplateId = ElementId.InvalidElementId; view.Name = plan.Item1;
                        view.DetailLevel = ViewDetailLevel.Fine; view.DisplayStyle = DisplayStyle.Shading; view.Discipline = ViewDiscipline.Coordination;
                        Parameter phase = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                        if (phase != null && !phase.IsReadOnly) phase.Set(scan.PhaseId);
                        ClashBounds box = plan.Item3;
                        view.SetSectionBox(new BoundingBoxXYZ { Min = new XYZ(box.Min[0], box.Min[1], box.Min[2]), Max = new XYZ(box.Max[0], box.Max[1], box.Max[2]) });
                        view.IsSectionBoxActive = true;
                        XYZ center = new XYZ((box.Min[0] + box.Max[0]) / 2, (box.Min[1] + box.Max[1]) / 2, (box.Min[2] + box.Max[2]) / 2);
                        XYZ forward = new XYZ(1, -1, -0.7).Normalize();
                        XYZ up = (XYZ.BasisZ - forward.Multiply(forward.DotProduct(XYZ.BasisZ))).Normalize();
                        view.SetOrientation(new ViewOrientation3D(center - forward.Multiply(100), up, forward));
                        HashSet<ElementId> visibleTargets = new HashSet<ElementId>();
                        foreach (Issue issue in plan.Item2)
                        {
                            Highlight(doc, view, Resolve(doc, issue.A), new Color(218, 55, 55), fill, visibleTargets, notices);
                            Highlight(doc, view, Resolve(doc, issue.B), new Color(30, 140, 210), fill, visibleTargets, notices);
                        }
                        Parameter description = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
                        if (description != null && !description.IsReadOnly)
                            description.Set("MCP clash scan " + scan.Id + " | checked " + DateTime.UtcNow.ToString("o") + " | " + string.Join(",", plan.Item2.Select(i => i.Id).ToArray()));
                        doc.Regenerate();
                        HashSet<ElementId> visible = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds());
                        foreach (ElementId id in visibleTargets)
                            if (!visible.Contains(id)) throw new InvalidOperationException("A target/link is not visible in the generated view (phase, option, workset or category). Rolled back: " + id.Value);
                        created.Add(new JObject(new JProperty("viewId", view.Id.Value), new JProperty("name", view.Name),
                            new JProperty("issueIds", new JArray(plan.Item2.Select(i => i.Id))), new JProperty("sectionBoxMm", BoundsJson(box)),
                            new JProperty("visibilityVerified", "Host targets and link instances; individual linked subelement visibility is not verified.")));
                    }
                    if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("View creation was not committed.");
                }
                catch { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); throw; }
            }
            return new JObject(new JProperty("status", "created"), new JProperty("dryRun", false), new JProperty("createdViews", created),
                new JProperty("notices", notices), new JProperty("saved", false), new JProperty("synchronized", false));
        }
        static void Highlight(Document doc, View3D view, Candidate c, Color color, FillPatternElement fill, HashSet<ElementId> targets, JArray notices)
        {
            Element element = c.Link == null ? c.Element : c.Link; targets.Add(element.Id);
            if (element.Category != null && view.CanCategoryBeHidden(element.Category.Id)) view.SetCategoryHidden(element.Category.Id, false);
            if (doc.IsWorkshared) view.SetWorksetVisibility(element.WorksetId, WorksetVisibility.Visible);
            OverrideGraphicSettings graphics = new OverrideGraphicSettings().SetProjectionLineColor(color).SetCutLineColor(color).SetProjectionLineWeight(5);
            if (fill != null) graphics.SetSurfaceForegroundPatternId(fill.Id).SetSurfaceForegroundPatternColor(color).SetCutForegroundPatternId(fill.Id).SetCutForegroundPatternColor(color);
            view.SetElementOverrides(element.Id, graphics);
            if (c.Link != null) notices.Add(new JObject(new JProperty("viewId", view.Id.Value), new JProperty("linkedElement", RefJson(c.Ref)),
                new JProperty("message", "Override applies to the entire link instance inside the section box. Linked subelement visibility/color is not individually guaranteed.")));
        }
        sealed class RollbackErrors : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                foreach (FailureMessageAccessor message in accessor.GetFailureMessages())
                    if (message.GetSeverity() == FailureSeverity.Error) return FailureProcessingResult.ProceedWithRollBack;
                return FailureProcessingResult.Continue;
            }
        }
    }
}
