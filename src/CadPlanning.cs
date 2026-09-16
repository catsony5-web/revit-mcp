using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    // Filesystem and name inference only. This class never loads Revit or a CAD engine.
    internal static class CadPlanning
    {
        static readonly RegexOptions RxOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        public const long MaxFileBytes = 256L * 1024L * 1024L;
        public const long MaxBatchBytes = 1024L * 1024L * 1024L;

        public static string LocalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Regex.IsMatch(path, @"^[A-Za-z]:[\\/]"))
                throw new ArgumentException("CAD paths must be absolute local paths.");
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
                throw new ArgumentException("UNC, device and alternate data stream paths are not supported.");
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
                throw new ArgumentException("UNC, device and alternate data stream paths are not supported.");
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(full));
            if (drive.DriveType == DriveType.Network)
                throw new ArgumentException("Mapped network drives are not supported. Copy the selected drawings locally first.");
            string ancestor = full;
            while (!string.IsNullOrEmpty(ancestor))
            {
                if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Symbolic links and junctions are not followed.");
                ancestor = Path.GetDirectoryName(ancestor);
            }
            return full;
        }

        public static string UnderRoot(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                throw new ArgumentException("relativePath must be relative to the inspected folder.");
            string prefix = LocalPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = LocalPath(Path.Combine(prefix, relative));
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("CAD path escapes the selected folder.");
            string walk = full;
            while (walk != null && walk.Length >= prefix.TrimEnd(Path.DirectorySeparatorChar).Length)
            {
                if ((File.Exists(walk) || Directory.Exists(walk)) &&
                    (File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Symbolic links and junctions are not followed: " + relative);
                walk = Path.GetDirectoryName(walk);
            }
            return full;
        }

        public static JArray Floors(string label)
        {
            string s = label ?? "";
            HashSet<string> floors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(s, @"지하\s*0*([1-9][0-9]?)\s*층|(?<![A-Z0-9])B\s*0*([1-9][0-9]?)(?:F|층)?(?![A-Z0-9])", RxOptions))
                floors.Add("B" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
            // Remove basement matches before the ordinary Korean floor pattern.
            s = Regex.Replace(s, @"지하\s*[0-9]+\s*층|(?<![A-Z0-9])B\s*[0-9]+(?:F|층)?(?![A-Z0-9])", " ", RxOptions);
            foreach (Match m in Regex.Matches(s, @"(?<![A-Z0-9])(?:지상\s*)?0*([1-9][0-9]?)\s*(?:층|F)(?![A-Z0-9])|(?<![A-Z0-9])(?:LEVEL\s*|L)0*([1-9][0-9]?)(?![A-Z0-9])", RxOptions))
                floors.Add((m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "F");
            if (Regex.IsMatch(s, @"옥상|지붕|(?<![A-Z])(?:ROOF|RF)(?![A-Z])", RxOptions)) floors.Add("RF");
            if (Regex.IsMatch(s, @"옥탑|(?<![A-Z])PH(?:층)?(?![A-Z])", RxOptions)) floors.Add("PH");
            return new JArray(floors.OrderBy(delegate(string x) { return x; }, StringComparer.Ordinal));
        }

        public static JObject Classify(string relative)
        {
            string path = relative.Replace('/', '\\');
            string stem = Path.GetFileNameWithoutExtension(path);
            List<string> disciplines = new List<string>();
            if (Regex.IsMatch(path, @"건축|ARCHITECT|(^|[\\ _-])ARCH([\\ _-]|$)", RxOptions)) disciplines.Add("architecture");
            if (Regex.IsMatch(path, @"구조|STRUCTUR", RxOptions)) disciplines.Add("structure");
            if (Regex.IsMatch(path, @"설비|기계|위생|공조|소방|MECHANICAL|PLUMBING|HVAC|(^|[\\ _-])MEP([\\ _-]|$)", RxOptions)) disciplines.Add("mep");
            if (Regex.IsMatch(path, @"전기|통신|ELECTRIC|TELECOM", RxOptions)) disciplines.Add("electrical");
            bool detail = Regex.IsMatch(stem, @"상세|확대|단면|입면|SECTION|ELEVATION|DETAIL", RxOptions);
            bool plan = Regex.IsMatch(stem, @"평면|FLOOR.?PLAN|PLAN", RxOptions) && !detail;
            bool backup = Regex.IsMatch(stem, @"(?:[ _.-](?:recover(?:ed)?|backup|bak|copy|복사본))(?:[ _.-]*[0-9]+)?$", RxOptions);
            bool archive = Regex.IsMatch(path, @"(^|\\)(?:원본|백업|BACKUP|ARCHIVE|OLD)(?:\\|$)", RxOptions);
            JArray floors = Floors(path);
            bool multi = floors.Count > 1 || Regex.IsMatch(path, @"기준층|전층|각층|TYPICAL|MULTI.?FLOOR|[0-9]\s*(?:층|F)?\s*(?:[,/~∼·\-–—−]|및|내지|부터|까지|에서|TO)\s*(?:지하|지상|B)?\s*[0-9]+\s*(?:층|F)|[0-9]\s*(?:층|F)\s*(?:부터|까지)", RxOptions);
            string canonical = Regex.Replace(stem, @"(?:[ _.-](?:recover(?:ed)?|backup|bak|copy|복사본))(?:[ _.-]*[0-9]+)?$", "", RxOptions);
            return new JObject(
                new JProperty("floorCandidates", floors),
                new JProperty("floor", floors.Count == 1 && !multi ? floors[0].ToString() : null),
                new JProperty("discipline", disciplines.Count == 1 ? disciplines[0] : "unknown"),
                new JProperty("disciplineCandidates", new JArray(disciplines)),
                new JProperty("drawingKind", detail ? "detailOrSection" : plan ? "floorPlan" : "unclassified"),
                new JProperty("multiFloorSuspected", multi),
                new JProperty("backupCandidate", backup),
                new JProperty("archiveCandidate", archive),
                new JProperty("duplicateKey", (Path.GetDirectoryName(path) + "\\" + canonical).ToLowerInvariant()),
                new JProperty("confidence", plan && floors.Count == 1 && !multi && !backup && !archive ? "filenameSuggestion" : "needsMapping"),
                new JProperty("inferenceBasis", "relative path and filename only; CAD geometry, title blocks and units have not been interpreted"));
        }

        public static JObject Inspect(string root, int maxFiles, int maxDepth)
        {
            root = LocalPath(root);
            if (root.TrimEnd(Path.DirectorySeparatorChar).Length <= 2)
                throw new ArgumentException("Select a drawing folder, not an entire drive root.");
            root = root.TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("The selected folder must not be a junction or symbolic link.");
            if (maxFiles < 1 || maxFiles > 2000 || maxDepth < 0 || maxDepth > 16)
                throw new ArgumentException("maxFiles must be 1..2000; maxDepth must be 0..16.");
            JArray files = new JArray(); JArray issues = new JArray(); bool truncated = false;
            Queue<KeyValuePair<string, int>> pending = new Queue<KeyValuePair<string, int>>();
            pending.Enqueue(new KeyValuePair<string, int>(root, 0));
            int visited = 0;
            while (pending.Count > 0 && !truncated)
            {
                KeyValuePair<string, int> dir = pending.Dequeue();
                try
                {
                    foreach (string path in Directory.EnumerateFileSystemEntries(dir.Key))
                    {
                        if (++visited > 20000) { truncated = true; break; }
                        try
                        {
                            FileAttributes attr = File.GetAttributes(path);
                            if ((attr & FileAttributes.ReparsePoint) != 0) { issues.Add("Skipped reparse point: " + path.Substring(root.Length + 1)); continue; }
                            if ((attr & FileAttributes.Directory) != 0)
                            {
                                if (dir.Value < maxDepth) pending.Enqueue(new KeyValuePair<string, int>(path, dir.Value + 1));
                                else { truncated = true; issues.Add("Depth limit reached: " + path.Substring(root.Length + 1)); }
                                continue;
                            }
                            string ext = Path.GetExtension(path);
                            if (!ext.Equals(".dwg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".dxf", StringComparison.OrdinalIgnoreCase)) continue;
                            if (files.Count >= maxFiles) { truncated = true; break; }
                            string relative = path.Substring(root.Length + 1);
                            FileInfo fi = new FileInfo(path);
                            JObject entry = Classify(relative);
                            entry["relativePath"] = relative; entry["sizeBytes"] = fi.Length;
                            entry["lastWriteUtcTicks"] = fi.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            entry["format"] = ext.Substring(1).ToLowerInvariant();
                            if (ext.Equals(".dwg", StringComparison.OrdinalIgnoreCase))
                            {
                                byte[] signature = new byte[6];
                                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    entry["headerSignature"] = stream.Read(signature, 0, 6) == 6 ? Encoding.ASCII.GetString(signature) : "shortFile";
                            }
                            files.Add(entry);
                        }
                        catch (Exception ex) { issues.Add("Unreadable entry: " + path.Substring(root.Length + 1) + " (" + ex.GetType().Name + ")"); }
                    }
                }
                catch (Exception ex) { issues.Add("Unreadable folder: " + dir.Key + " (" + ex.GetType().Name + ")"); }
            }
            foreach (IGrouping<string, JToken> group in files.GroupBy(delegate(JToken x) { return (string)x["duplicateKey"]; }, StringComparer.OrdinalIgnoreCase))
                if (group.Count() > 1) foreach (JToken entry in group) entry["duplicateCandidates"] = new JArray(group.Select(delegate(JToken x) { return (string)x["relativePath"]; }));
            return new JObject(new JProperty("root", root), new JProperty("files", files),
                new JProperty("count", files.Count), new JProperty("truncated", truncated), new JProperty("issues", issues),
                new JProperty("cadContentParsed", false),
                new JProperty("nextStep", "Review candidates, choose drawings and provide floor, units and alignment mappings to plan_cad_layout. No model changes were made."));
        }

        public static string Fingerprint(string fullPath)
        {
            FileInfo fi = new FileInfo(fullPath);
            if (!fi.Exists) throw new FileNotFoundException("CAD file is missing.", fullPath);
            if (fi.Length > MaxFileBytes) throw new ArgumentException("Selected CAD file exceeds the 256 MiB content verification limit. Split or reduce this drawing first.");
            using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
            using (SHA256 sha = SHA256.Create())
            {
                if (stream.Length > MaxFileBytes) throw new ArgumentException("Selected CAD file exceeds the 256 MiB content verification limit.");
                return "sha256:" + stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
        }

        public static void ValidateTargetReferences(JObject item)
        {
            foreach (string kind in new[] { "Level", "View" })
            {
                string flag = "create" + kind; string id = char.ToLowerInvariant(kind[0]) + kind.Substring(1) + "Id";
                if (item[flag] == null || item[flag].Type != JTokenType.Boolean || item[id] == null || item[id].Type != JTokenType.Integer)
                    throw new ArgumentException("CAD plan requires boolean " + flag + " and integer " + id + ".");
                long value = item[id].Value<long>();
                if (value < 0 || value > int.MaxValue || ((bool)item[flag] ? value != 0 : value == 0))
                    throw new ArgumentException(flag + " must use ID zero for creation, or a positive existing ID when false.");
            }
            if ((bool)item["createLevel"] && !(bool)item["createView"])
                throw new ArgumentException("A new level cannot reference an existing plan view.");
        }

        public static string BlockingReason(JObject file, bool explicitlySelected, bool allowNonPlan, bool multiFloorSeparated)
        {
            if ((bool)file["multiFloorSuspected"] && !multiFloorSeparated)
                return "MULTI_FLOOR: export a separate model-space drawing per floor. This tool does not split or decode a multi-floor DWG.";
            if (!explicitlySelected && ((bool)file["backupCandidate"] || (bool)file["archiveCandidate"] || file["duplicateCandidates"] != null))
                return "REVIEW_DUPLICATE_OR_ARCHIVE: explicitly select the intended file.";
            if ((string)file["drawingKind"] != "floorPlan" && !allowNonPlan)
                return "NOT_A_FLOOR_PLAN: detail/section/unclassified drawings require an explicit mapping with allowNonPlan=true.";
            return null;
        }
    }
}
