using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    internal sealed class RequestJob
    {
        public string Id, Fingerprint, Tool, State, Error;
        public bool Durable;
        public DateTime CreatedUtc, StartedUtc, FinishedUtc;
        public JToken Result;
        public Func<object> Work;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }
    // No Revit dependencies: durable state transitions are independently testable.
    internal sealed class RequestQueue
    {
        readonly object gate = new object();
        readonly Dictionary<string, RequestJob> jobs = new Dictionary<string, RequestJob>(StringComparer.Ordinal);
        readonly Queue<RequestJob> pending = new Queue<RequestJob>();
        readonly string directory;
        readonly int capacity;
        bool stopped, paused;
        public RequestQueue(string journalDirectory, int maxQueued) { directory = journalDirectory; capacity = maxQueued; }
        public static string Fingerprint(string tool, JObject args)
        {
            JObject copy = (JObject)args.DeepClone();
            copy.Remove("requestId"); copy.Remove("timeoutMs");
            return Hash(tool + "\n" + Canonical(copy).ToString(Formatting.None));
        }
        static JToken Canonical(JToken token)
        {
            JObject obj = token as JObject;
            if (obj != null) { JObject result = new JObject(); foreach (JProperty p in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)) result[p.Name] = Canonical(p.Value); return result; }
            JArray array = token as JArray;
            if (array != null) return new JArray(array.Select(Canonical));
            return token.DeepClone();
        }
        static string Hash(string value) { using (SHA256 h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        static void CheckId(string id) { if (id == null || !Regex.IsMatch(id, "^[A-Za-z0-9_.-]{1,128}$")) throw new ArgumentException("requestId must be 1-128 letters, digits, dot, underscore or dash."); }
        string FileName(string id) { return Path.Combine(directory, Hash(id) + ".json"); }
        static bool Terminal(RequestJob j) { return j.State != "queued" && j.State != "running"; }
        public RequestJob Accept(string id, string fingerprint, string tool, bool durable, Func<object> work, out bool fresh)
        {
            CheckId(id);
            lock (gate)
            {
                fresh = false;
                RequestJob existing = Find(id);
                if (existing != null)
                {
                    if (existing.Fingerprint != fingerprint) throw new InvalidOperationException("requestId already belongs to a different operation. Do not reuse it with changed arguments.");
                    return existing;
                }
                if (stopped || paused) throw new InvalidOperationException("Request queue is stopped or paused.");
                if (pending.Count >= capacity) throw new InvalidOperationException("Request queue is full. Wait for existing requests; do not generate duplicate writes.");
                if (jobs.Count >= 1024)
                {
                    foreach (string key in jobs.Where(p => Terminal(p.Value)).Take(256).Select(p => p.Key).ToArray()) jobs.Remove(key);
                    if (jobs.Count >= 1024) throw new InvalidOperationException("Too many active requests.");
                }
                RequestJob job = new RequestJob { Id = id, Fingerprint = fingerprint, Tool = tool, Durable = durable, Work = work, State = "queued", CreatedUtc = DateTime.UtcNow };
                // Initial admission is an exclusive rename, including across processes.
                // Never replace a journal another queue may have admitted concurrently.
                try { Persist(job, true); }
                catch (IOException)
                {
                    RequestJob winner = Find(id);
                    if (winner == null) throw;
                    if (winner.Fingerprint != fingerprint) throw new InvalidOperationException("requestId already belongs to a different operation.");
                    return winner;
                }
                jobs.Add(id, job); pending.Enqueue(job); fresh = true;
                return job;
            }
        }
        RequestJob Find(string id)
        {
            RequestJob job;
            if (jobs.TryGetValue(id, out job)) return job;
            string path = FileName(id);
            if (!File.Exists(path)) return null;
            JObject data = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            if ((string)data["requestId"] != id) throw new InvalidOperationException("Invalid request journal identity.");
            job = new RequestJob { Id = id, Fingerprint = (string)data["fingerprint"], Tool = (string)data["tool"], Durable = true, State = (string)data["status"], Error = (string)data["error"], Result = data["result"], CreatedUtc = (DateTime)data["createdUtc"] };
            if (job.State == "queued" || job.State == "running") { job.State = "unknown_after_restart"; job.Error = "Previous process ended before durable completion. Inspect model/state; this request will not be replayed."; }
            job.Done.Set(); jobs[id] = job;
            return job;
        }
        public bool HasPending { get { lock (gate) return pending.Count > 0; } }
        public bool ExecuteOne()
        {
            RequestJob job = null;
            lock (gate)
            {
                if (stopped || paused) return false;
                while (pending.Count > 0) { RequestJob next = pending.Dequeue(); if (next.State == "queued") { job = next; break; } }
                if (job == null) return false;
                job.State = "running"; job.StartedUtc = DateTime.UtcNow;
                try { Persist(job); }
                catch (Exception ex) { job.State = "failed_before_execution"; job.Error = ex.Message; job.Work = null; job.Done.Set(); return true; }
            }
            JToken result = null; string error = null;
            try { object value = job.Work(); result = value == null ? JValue.CreateNull() : (value as JToken) ?? JToken.FromObject(value); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            lock (gate)
            {
                job.Result = result; job.Error = error; job.State = error == null ? "succeeded" : "failed";
                job.FinishedUtc = DateTime.UtcNow; job.Work = null;
                try { Persist(job); }
                catch (Exception ex) { job.State = "completion_not_persisted"; job.Error = "Operation ended, but durable result could not be saved: " + ex.Message + ". Do not replay automatically."; }
                job.Done.Set();
            }
            return true;
        }
        public bool CancelQueued(string id, string reason)
        {
            CheckId(id);
            lock (gate)
            {
                RequestJob job = Find(id);
                if (job == null || job.State != "queued") return false;
                job.State = "cancelled_before_execution"; job.Error = reason; job.FinishedUtc = DateTime.UtcNow; job.Work = null;
                try { Persist(job); } finally { job.Done.Set(); }
                return true;
            }
        }
        public void Stop()
        {
            lock (gate) { stopped = true; Pause("Server stopped before execution."); }
        }
        public void Pause(string reason)
        {
            lock (gate)
            {
                paused = true;
                foreach (RequestJob job in pending.ToArray()) if (job.State == "queued") { try { CancelQueued(job.Id, reason); } catch { } }
                pending.Clear();
            }
        }
        public void Resume()
        {
            lock (gate) { if (stopped) throw new InvalidOperationException("Request queue is permanently stopped."); paused = false; }
        }
        public JObject Status(string id)
        {
            CheckId(id);
            lock (gate) { RequestJob job = Find(id); if (job == null) return new JObject(new JProperty("requestId", id), new JProperty("status", "not_found")); return Snapshot(job); }
        }
        public JObject Snapshot(RequestJob job)
        {
            lock (gate)
            {
                JObject data = new JObject(new JProperty("requestId", job.Id), new JProperty("tool", job.Tool), new JProperty("status", job.State), new JProperty("createdUtc", job.CreatedUtc), new JProperty("error", job.Error), new JProperty("result", job.Result == null ? null : job.Result.DeepClone()));
                data["replayAllowed"] = false;
                if (job.StartedUtc != default(DateTime)) data["startedUtc"] = job.StartedUtc;
                if (job.FinishedUtc != default(DateTime)) data["finishedUtc"] = job.FinishedUtc;
                return data;
            }
        }
        void Persist(RequestJob job, bool initialAdmission = false)
        {
            if (!job.Durable) return;
            Directory.CreateDirectory(directory);
            JObject data = Snapshot(job); data["fingerprint"] = job.Fingerprint;
            string path = FileName(job.Id); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(data.ToString(Formatting.None));
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (!initialAdmission && File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
