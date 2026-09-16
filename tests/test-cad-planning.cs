using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitMcp;

internal static class CadPlanningTests
{
    static int count;
    static void Check(bool pass, string label) { if (!pass) throw new Exception(label); count++; Console.WriteLine("PASS: " + label); }
    static void Throws(Action action, string label) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, label); }
    static JObject Target(bool createLevel, int levelId, bool createView, int viewId)
    {
        return new JObject(new JProperty("createLevel", createLevel), new JProperty("levelId", levelId), new JProperty("createView", createView), new JProperty("viewId", viewId));
    }
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        Check((string)CadPlanning.Floors("지하 01층 평면도")[0] == "B1", "Korean basement resolves without positive-floor collision");
        Check((string)CadPlanning.Floors("B02F plan")[0] == "B2", "English basement resolves with padded number");
        Check((string)CadPlanning.Floors("지상 3층 평면도")[0] == "3F", "Korean above-ground floor");
        Check(CadPlanning.Floors("A33-005 주단면도-6.7").Count == 0, "Drawing/detail numbers are never floor numbers");
        Check(CadPlanning.Floors("확대평면도-1(계단실-2)").Count == 0, "Stair/detail identifiers are never floors");
        Check((bool)CadPlanning.Classify(@"건축\1F_2F 평면도.dwg")["multiFloorSuspected"], "Multiple explicit floors require review");
        Check((bool)CadPlanning.Classify(@"건축\1~3층 평면도.dwg")["multiFloorSuspected"], "Floor ranges cannot become a single floor");
        Check((bool)CadPlanning.Classify(@"건축\지상 1,2층 평면도.dwg")["multiFloorSuspected"], "Comma-separated floors remain ambiguous");
        foreach (string expression in new[] { "2-5층", "지상2-5층", "2–5F", "2—5F", "2−5층", "지하2-5층", "지하2–지하5층", "B2-B5F", "2 및 5층", "2부터5층까지", "지하2부터지하5층까지", "2층까지", "2층부터" })
        {
            JObject range = CadPlanning.Classify(@"건축\" + expression + " 평면도.dwg");
            Check((bool)range["multiFloorSuspected"] && range["floor"].Type == JTokenType.Null && CadPlanning.BlockingReason(range, false, false, false).StartsWith("MULTI_FLOOR"), "Ambiguous range never auto-selects its last floor: " + expression);
        }
        foreach (string folder in new[] { "2~5층", "2-5층", "지하2부터5층까지" })
        {
            JObject range = CadPlanning.Classify(@"건축\" + folder + @"\평면도.dwg");
            Check((bool)range["multiFloorSuspected"] && range["floor"].Type == JTokenType.Null, "Folder floor range cannot select last floor: " + folder);
        }
        Check((bool)CadPlanning.Classify(@"건축\기준층 평면도.dwg")["multiFloorSuspected"], "Typical-floor sheets require explicit verification");
        Check((string)CadPlanning.Classify(@"구조\3F 평면도.dwg")["discipline"] == "structure", "Discipline follows structured folder metadata");
        Check((string)CadPlanning.Classify(@"설비\B1 평면도.dwg")["discipline"] == "mep", "MEP discipline recognized");
        Check((string)CadPlanning.Classify(@"건축\구조\1F 평면도.dwg")["discipline"] == "unknown", "Conflicting discipline metadata is not guessed");
        Check((string)CadPlanning.Classify(@"건축\1F 확대평면도.dwg")["drawingKind"] == "detailOrSection", "Expanded detail plans are not automatic floor plans");
        Check((bool)CadPlanning.Classify(@"원본\1F 평면도.dwg")["archiveCandidate"], "Archive folder flagged");
        Check((bool)CadPlanning.Classify(@"건축\1F 평면도_recover.dwg")["backupCandidate"], "Recovery files flagged");
        JObject multi = CadPlanning.Classify(@"건축\1F_2F 평면도.dwg");
        Check(CadPlanning.BlockingReason(multi, true, true, false).StartsWith("MULTI_FLOOR"), "Explicit selection alone does not resolve multi-floor contents");
        Check(CadPlanning.BlockingReason(multi, true, false, true) == null, "User-confirmed single-floor file can override a misleading filename");
        CadPlanning.ValidateTargetReferences(Target(false, 42, false, 43));
        CadPlanning.ValidateTargetReferences(Target(false, 42, true, 0));
        CadPlanning.ValidateTargetReferences(Target(true, 0, true, 0));
        Check(true, "Existing targets and explicit zero-ID creation forms accepted");
        Throws(delegate { CadPlanning.ValidateTargetReferences(Target(true, 42, true, 0)); }, "Creation flag cannot bypass validation of an existing level ID");
        Throws(delegate { CadPlanning.ValidateTargetReferences(Target(false, 42, true, 43)); }, "Creation flag cannot bypass validation of an existing view ID");
        Throws(delegate { CadPlanning.ValidateTargetReferences(Target(false, 0, true, 0)); }, "Zero level ID cannot silently create a level");
        Throws(delegate { CadPlanning.ValidateTargetReferences(Target(false, 42, false, 0)); }, "Zero view ID cannot silently create a view");
        Throws(delegate { CadPlanning.ValidateTargetReferences(Target(true, 0, false, 43)); }, "A new level cannot reuse an existing view");
        Throws(delegate { CadPlanning.ValidateTargetReferences(Target(false, -42, true, 0)); }, "Negative target IDs rejected");
        JObject invalidFlag = Target(true, 0, true, 0); invalidFlag["createLevel"] = "true";
        Throws(delegate { CadPlanning.ValidateTargetReferences(invalidFlag); }, "String flags in untrusted plans rejected");
        JObject invalidId = Target(false, 42, false, 43); invalidId["levelId"] = 42.5;
        Throws(delegate { CadPlanning.ValidateTargetReferences(invalidId); }, "Fractional target IDs in untrusted plans rejected");
        Throws(delegate { CadPlanning.ValidateTargetReferences(new JObject()); }, "Missing creation flags and IDs rejected");
        Throws(delegate { CadPlanning.UnderRoot(root, @"..\outside.dwg"); }, "Parent traversal blocked");
        Throws(delegate { CadPlanning.LocalPath(@"\\example\share\drawing.dwg"); }, "UNC network path blocked");
        Throws(delegate { CadPlanning.LocalPath("relative.dwg"); }, "Relative root blocked");
        Throws(delegate { CadPlanning.LocalPath("C:relative.dwg"); }, "Drive-relative path blocked");
        Throws(delegate { CadPlanning.Inspect(Path.GetPathRoot(root), 20, 3); }, "Entire drive scan blocked");
        Throws(delegate { CadPlanning.LocalPath(Path.Combine(root, "file.dwg:stream")); }, "Alternate data stream blocked");
        File.WriteAllText(Path.Combine(root, "1F floor plan.dwg"), "AC1032FAKE");
        File.WriteAllText(Path.Combine(root, "1F floor plan_recover.dwg"), "AC1032FAKE");
        File.WriteAllText(Path.Combine(root, "ignored.txt"), "not CAD");
        JObject scan = CadPlanning.Inspect(root, 20, 3); JArray files = (JArray)scan["files"];
        Check(files.Count == 2 && !(bool)scan["truncated"], "Bounded scan includes CAD only");
        Check(files.All(delegate(JToken f) { return ((JArray)f["duplicateCandidates"]).Count == 2; }), "Original/recover duplicate pairs flagged on both files");
        Check((string)files[0]["headerSignature"] == "AC1032" && !(bool)scan["cadContentParsed"], "Header probe never claims CAD content interpretation");
        Check(CadPlanning.BlockingReason((JObject)files[0], false, false, false).StartsWith("REVIEW_DUPLICATE"), "Duplicate candidate prevents automatic placement");
        Check((bool)CadPlanning.Inspect(root, 1, 3)["truncated"], "File limit reports incomplete scan");
        string fingerprint = CadPlanning.Fingerprint(Path.Combine(root, "1F floor plan.dwg"));
        File.AppendAllText(Path.Combine(root, "1F floor plan.dwg"), "CHANGED");
        Check(fingerprint != CadPlanning.Fingerprint(Path.Combine(root, "1F floor plan.dwg")), "Source freshness changes when drawing changes");
        string changedPath = Path.Combine(root, "1F floor plan.dwg");
        string contentHash = CadPlanning.Fingerprint(changedPath);
        DateTime timestamp = File.GetLastWriteTimeUtc(changedPath);
        byte[] bytes = File.ReadAllBytes(changedPath); bytes[8] = (byte)'Q'; File.WriteAllBytes(changedPath, bytes); File.SetLastWriteTimeUtc(changedPath, timestamp);
        Check(contentHash.StartsWith("sha256:") && contentHash != CadPlanning.Fingerprint(changedPath), "SHA256 detects changed content with identical file length and restored timestamp");
        Directory.CreateDirectory(Path.Combine(root, "deeper"));
        File.WriteAllText(Path.Combine(root, "deeper", "2F floor plan.dxf"), "fake DXF");
        Check((bool)CadPlanning.Inspect(root, 20, 0)["truncated"], "Depth limit reports incomplete scan");
        Throws(delegate { CadPlanning.Inspect(root, 2001, 3); }, "Unbounded file count rejected");
        Console.WriteLine(count + " CAD filesystem/planning checks passed. Revit was not loaded or contacted.");
        return 0;
    }
}
