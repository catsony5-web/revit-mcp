// Tests the actual DataStore source in an isolated executable directory.
// Collection read/modify/write locking is single-process only. These tests do
// not imply cross-process safety; request-journal admission is a separate path.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RevitMcp;

namespace RevitMcp
{
    internal static class Log { public static void Error(string message, Exception error) { } }
}
static class DataStoreTests
{
    static int assertions;
    static void Check(bool condition, string message)
    {
        assertions++; if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
    static void ThrowsIo(Action action, string message)
    {
        bool thrown = false; try { action(); } catch (IOException) { thrown = true; }
        Check(thrown, message);
    }
    static string FileFor(string collection) { return Path.Combine(DataStore.Root, collection + ".json"); }
    static JObject Record(string collection, string key)
    { return DataStore.Load(collection).OfType<JObject>().Single(row => (string)row["key"] == key); }

    static void Main()
    {
        string executableDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Check(Path.GetDirectoryName(DataStore.Root) == executableDirectory, "store isolated beside temporary test executable");
        Check(DataStore.Load("absent").Count == 0 && !File.Exists(FileFor("absent")), "missing collection returns empty without creating a file");
        Check(DataStore.Put("replace", "a", new JValue("old"), new JObject(new JProperty("source", "first"))), "new key reports added");
        JObject initial = Record("replace", "a");
        Check(initial["createdAt"] != null && initial["updatedAt"] != null, "new record has creation and update timestamps");
        // A deliberately old timestamp makes preservation distinguishable even
        // when the entire test completes inside one wall-clock second.
        JArray seeded = DataStore.Load("replace"); seeded[0]["createdAt"] = "2001-02-03 04:05:06";
        File.WriteAllText(FileFor("replace"), seeded.ToString(), new UTF8Encoding(false));
        byte[] beforeReplacement = File.ReadAllBytes(FileFor("replace"));
        Check(!DataStore.Put("replace", "a", new JValue("new"), new JObject(new JProperty("source", "second"))), "replacement reports existing key");
        JObject updated = Record("replace", "a");
        Check((string)updated["createdAt"] == "2001-02-03 04:05:06" && (string)updated["value"] == "new" && (string)updated["meta"]["source"] == "second", "replacement preserves createdAt and updates value/meta");
        string backup = FileFor("replace") + ".bak";
        Check(File.Exists(backup) && File.ReadAllBytes(backup).SequenceEqual(beforeReplacement), "atomic replacement retains exact previous bytes in backup");
        Check((string)JArray.Parse(File.ReadAllText(backup))[0]["value"] == "old", "retained backup parses and contains prior value");

        const int workerCount = 4, writesPerWorker = 8;
        ManualResetEventSlim start = new ManualResetEventSlim(false);
        ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
        Thread[] workers = new Thread[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            int worker = w;
            workers[w] = new Thread(delegate()
            {
                try
                {
                    if (!start.Wait(5000)) throw new Exception("start barrier timed out");
                    for (int i = 0; i < writesPerWorker; i++)
                        if (!DataStore.Put("concurrent", worker + "-" + i, new JValue(worker * 100 + i), null))
                            throw new Exception("unique concurrent key unexpectedly replaced");
                }
                catch (Exception ex) { errors.Enqueue(ex); }
            });
            workers[w].IsBackground = true; workers[w].Start();
        }
        start.Set();
        bool joined = true; foreach (Thread worker in workers) if (!worker.Join(15000)) joined = false;
        Check(joined && errors.IsEmpty, "four simultaneous same-process writers complete without error");
        JArray concurrent = DataStore.Load("concurrent");
        Check(concurrent.Count == workerCount * writesPerWorker && concurrent.Select(r => (string)r["key"]).Distinct().Count() == workerCount * writesPerWorker, "concurrent Put preserves every distinct key");
        bool valuesMatch = true;
        for (int w = 0; w < workerCount; w++) for (int i = 0; i < writesPerWorker; i++)
            if ((int)Record("concurrent", w + "-" + i)["value"] != w * 100 + i) valuesMatch = false;
        Check(valuesMatch, "concurrent values remain paired with their own keys");
        Check(JArray.Parse(File.ReadAllText(FileFor("concurrent") + ".bak")).Count == workerCount * writesPerWorker - 1, "concurrent replacement leaves parseable immediate predecessor backup");
        Check(DataStore.Delete("concurrent", "0-0") == 1 && DataStore.Load("concurrent").Count == workerCount * writesPerWorker - 1, "Delete removes exactly the requested record");
        byte[] beforeNoOpDelete = File.ReadAllBytes(FileFor("concurrent"));
        Check(DataStore.Delete("concurrent", "missing") == 0 && File.ReadAllBytes(FileFor("concurrent")).SequenceEqual(beforeNoOpDelete), "missing-key Delete reports zero and preserves file");

        DataStore.Put("broken", "old", new JValue(1), null); DataStore.Put("broken", "old", new JValue(2), null);
        string brokenPath = FileFor("broken"); byte[] goodBackup = File.ReadAllBytes(brokenPath + ".bak");
        File.WriteAllText(brokenPath, "{ malformed data", new UTF8Encoding(false)); byte[] brokenBytes = File.ReadAllBytes(brokenPath);
        ThrowsIo(delegate { DataStore.Load("broken"); }, "corrupt JSON load fails instead of returning empty");
        ThrowsIo(delegate { DataStore.Put("broken", "new", new JValue(3), null); }, "Put refuses to overwrite corrupt JSON");
        Check(File.ReadAllBytes(brokenPath).SequenceEqual(brokenBytes) && File.ReadAllBytes(brokenPath + ".bak").SequenceEqual(goodBackup), "failed corrupt Put preserves damaged file and good backup");
        ThrowsIo(delegate { DataStore.Delete("broken", "old"); }, "Delete refuses a corrupt collection");
        Check(File.ReadAllBytes(brokenPath).SequenceEqual(brokenBytes), "failed corrupt Delete preserves original bytes");

        foreach (string contents in new[] { "", " \r\n\t" })
        {
            File.WriteAllText(FileFor("empty"), contents, new UTF8Encoding(false)); byte[] emptyBytes = File.ReadAllBytes(FileFor("empty"));
            ThrowsIo(delegate { DataStore.Put("empty", "new", new JValue(1), null); }, "Put rejects empty or whitespace collection");
            Check(File.ReadAllBytes(FileFor("empty")).SequenceEqual(emptyBytes) && !File.Exists(FileFor("empty") + ".bak"), "failed empty Put preserves source and creates no replacement backup");
        }
        File.WriteAllText(FileFor("object-root"), "{\"not\":\"an array\"}", new UTF8Encoding(false));
        ThrowsIo(delegate { DataStore.Put("object-root", "new", new JValue(1), null); }, "wrong JSON root type is not silently replaced");
        Check(Directory.GetFiles(DataStore.Root, "*.tmp").Length == 0, "normal writes and refused writes leave no temporary files");
        Console.WriteLine(assertions + " isolated DataStore assertions passed (single-process concurrency only).");
    }
}
