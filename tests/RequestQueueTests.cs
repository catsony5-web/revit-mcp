using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RevitMcp;

class RequestQueueTests
{
    static int checks;
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
    static void Throws(Action action, string label) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, label); }
    static void Main(string[] args)
    {
        string root = args[0]; Directory.CreateDirectory(root);
        RequestQueue q = new RequestQueue(root, 8); bool fresh;
        int effects = 0;
        RequestJob a = q.Accept("once", "fp1", "write", true, delegate { effects++; return 42; }, out fresh);
        Check(fresh && a.State == "queued", "new request accepted once");
        RequestJob duplicate = q.Accept("once", "fp1", "write", true, delegate { effects += 99; return 0; }, out fresh);
        Check(!fresh && object.ReferenceEquals(a, duplicate), "queued duplicate reuses same job");
        Throws(delegate { q.Accept("once", "changed", "write", true, delegate { return 0; }, out fresh); }, "same ID with changed payload rejected");
        Check(q.ExecuteOne() && effects == 1 && a.State == "succeeded", "one execution produces result");
        q.Accept("once", "fp1", "write", true, delegate { effects += 99; return 0; }, out fresh);
        Check(!fresh && effects == 1 && !q.ExecuteOne(), "completed retry never runs again");
        RequestQueue restored = new RequestQueue(root, 8);
        RequestJob recovered = restored.Accept("once", "fp1", "write", true, delegate { effects++; return 0; }, out fresh);
        Check(!fresh && recovered.State == "succeeded" && (int)recovered.Result == 42, "completed result survives restart");
        Check(!restored.ExecuteOne() && effects == 1, "recovered result not enqueued");

        q.Accept("cancel", "cancel-fp", "write", true, delegate { effects++; return 0; }, out fresh);
        Check(q.CancelQueued("cancel", "client timeout"), "queued timeout can cancel");
        q.ExecuteOne(); Check(effects == 1 && (string)q.Status("cancel")["status"] == "cancelled_before_execution", "cancelled queued job never modifies data");
        q.Accept("cancel", "cancel-fp", "write", true, delegate { effects++; return 0; }, out fresh);
        Check(!fresh && !q.ExecuteOne(), "cancelled operation requires a deliberate new ID");

        ManualResetEventSlim entered = new ManualResetEventSlim(false), release = new ManualResetEventSlim(false);
        q.Accept("running", "run-fp", "write", true, delegate { entered.Set(); release.Wait(5000); effects++; return "done"; }, out fresh);
        Thread thread = new Thread(delegate() { q.ExecuteOne(); }); thread.Start();
        Check(entered.Wait(5000), "execution enters running state");
        Check(!q.CancelQueued("running", "cancel"), "running work cannot falsely report cancellation");
        Check((string)q.Status("running")["status"] == "running", "running request is queryable");
        RequestQueue afterCrash = new RequestQueue(root, 8);
        Check((string)afterCrash.Status("running")["status"] == "unknown_after_restart", "interrupted write reported as unknown after restart");
        afterCrash.Accept("running", "run-fp", "write", true, delegate { effects += 99; return 0; }, out fresh);
        Check(!fresh && !afterCrash.ExecuteOne(), "uncertain prior execution never replays");
        release.Set(); Check(thread.Join(5000) && effects == 2, "running work finishes once and saves result");

        q.Accept("error", "error-fp", "write", true, delegate { throw new InvalidOperationException("test failure"); }, out fresh);
        q.ExecuteOne(); Check((string)q.Status("error")["status"] == "failed", "exceptions produce queryable failure");
        RequestQueue small = new RequestQueue(Path.Combine(root,"small"), 1);
        small.Accept("a", "a", "write", true, delegate { return 1; }, out fresh);
        Throws(delegate { small.Accept("b", "b", "write", true, delegate { return 2; }, out fresh); }, "queue capacity prevents unlimited accumulation");
        small.Stop(); Check(!small.ExecuteOne(), "stop cancels pending operations");
        Throws(delegate { small.Accept("b", "b", "write", true, delegate { return 2; }, out fresh); }, "stopped queue rejects new operations");
        Throws(delegate { small.Resume(); }, "permanent shutdown cannot resume");
        RequestQueue pausable = new RequestQueue(Path.Combine(root,"pause"), 2);
        pausable.Accept("before-stop", "a", "write", true, delegate { effects += 99; return 1; }, out fresh);
        pausable.Pause("Server stopped.");
        Check(!pausable.HasPending && !pausable.ExecuteOne(), "pause drains queued jobs before execution");
        Check((string)pausable.Status("before-stop")["status"] == "cancelled_before_execution", "pause persists cancellation");
        Throws(delegate { pausable.Accept("paused", "b", "write", true, delegate { return 2; }, out fresh); }, "pause rejects admission");
        pausable.Resume(); pausable.Accept("after-start", "c", "write", true, delegate { return 3; }, out fresh);
        Check(fresh && pausable.ExecuteOne(), "resume admits fresh jobs without replaying stopped jobs");

        string raceRoot = Path.Combine(root,"race");
        RequestQueue firstQueue = new RequestQueue(raceRoot, 8), secondQueue = new RequestQueue(raceRoot, 8);
        ManualResetEventSlim start = new ManualResetEventSlim(false);
        Exception raceError = null; int admissions = 0, raceEffects = 0;
        Thread[] contenders = new[] { firstQueue, secondQueue }.Select(queue => new Thread(delegate()
        {
            try { start.Wait(); bool accepted; queue.Accept("shared", "same", "write", true, delegate { Interlocked.Increment(ref raceEffects); return 1; }, out accepted); if (accepted) Interlocked.Increment(ref admissions); }
            catch (Exception ex) { raceError = ex; }
        })).ToArray();
        foreach (Thread contender in contenders) contender.Start(); start.Set();
        foreach (Thread contender in contenders) Check(contender.Join(5000), "independent queue admission finishes");
        Check(raceError == null && admissions == 1, "exclusive journal admission admits one owner across queue instances");
        firstQueue.ExecuteOne(); secondQueue.ExecuteOne();
        Check(raceEffects == 1, "independent queues cannot execute the same durable request twice");
        Throws(delegate { q.Status("../../escape"); }, "journal ID path traversal rejected");
        string first = RequestQueue.Fingerprint("tool", JObject.Parse("{\"b\":2,\"a\":1,\"timeoutMs\":5}"));
        string second = RequestQueue.Fingerprint("tool", JObject.Parse("{\"a\":1,\"b\":2,\"timeoutMs\":500}"));
        Check(first == second, "canonical fingerprint ignores property order and wait timeout");
        Check(first != RequestQueue.Fingerprint("other", JObject.Parse("{\"a\":1,\"b\":2}")), "fingerprint binds tool name");
        string corruptRoot=Path.Combine(root,"corrupt"); RequestQueue corrupt = new RequestQueue(corruptRoot,8);
        corrupt.Accept("broken", "x", "write", true, delegate { return 1; }, out fresh);
        File.WriteAllText(Directory.GetFiles(corruptRoot,"*.json")[0], "broken json");
        RequestQueue corruptedRestart = new RequestQueue(corruptRoot,8);
        Throws(delegate { corruptedRestart.Accept("broken", "x", "write", true, delegate { return 1; }, out fresh); }, "corrupt journal fails closed");
        string unwritable=Path.Combine(root,"not-a-directory"); File.WriteAllText(unwritable,"x");
        RequestQueue brokenStore=new RequestQueue(unwritable,8);
        Throws(delegate { brokenStore.Accept("write", "x", "write", true, delegate { effects++; return 1; }, out fresh); }, "journal failure rejects write before execution");
        Check(!brokenStore.ExecuteOne(), "failed persistence never queues a model mutation");
        Console.WriteLine(checks + " request lifecycle checks passed.");
    }
}
