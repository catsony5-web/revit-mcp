using System;
using System.IO;
using System.Diagnostics;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    internal sealed class Dispatcher : IExternalEventHandler
    {
        static Dispatcher instance;
        static ExternalEvent externalEvent;
        static RequestQueue queue;
        static readonly object admissionGate = new object();
        static bool accepting;
        static long serverGeneration;
        [ThreadStatic] static UIApplication currentApplication;
        [ThreadStatic] static long? requestGeneration;
        public static bool IsReady { get { lock (admissionGate) return instance != null && externalEvent != null; } }
        public static void Initialize(UIControlledApplication app)
        {
            instance = new Dispatcher();
            queue = new RequestQueue(Path.Combine(DataStore.Root, "requests"), 64);
            queue.Pause("HTTP server has not started.");
            externalEvent = ExternalEvent.Create(instance);
            app.Idling += OnIdling;
            DocumentGuard.Initialize(app);
        }
        public static void Shutdown(UIControlledApplication app)
        {
            app.Idling -= OnIdling;
            DocumentGuard.Shutdown(app);
            lock (admissionGate)
            {
                accepting = false; serverGeneration++;
                if (queue != null) queue.Stop();
                if (externalEvent != null) externalEvent.Dispose();
                externalEvent = null; instance = null;
            }
        }
        static void OnIdling(object sender, IdlingEventArgs args)
        {
            lock (admissionGate)
                if (accepting && instance != null && externalEvent != null && queue.HasPending) externalEvent.Raise();
        }
        public static long Resume()
        {
            lock (admissionGate)
            {
                if (instance == null || externalEvent == null) throw new InvalidOperationException("Revit dispatcher is not initialized.");
                queue.Resume(); accepting = true;
                return ++serverGeneration;
            }
        }
        public static void Pause()
        {
            lock (admissionGate)
            {
                accepting = false; serverGeneration++;
                if (queue != null) queue.Pause("HTTP server stopped before execution.");
            }
        }
        static void CheckAdmission(long? generation)
        {
            if (instance == null || externalEvent == null) throw new InvalidOperationException("Revit dispatcher is not initialized.");
            if (!accepting) throw new InvalidOperationException("HTTP server is stopped; request was not accepted.");
            if (generation.HasValue && generation.Value != serverGeneration)
                throw new InvalidOperationException("Connection belongs to an earlier server session; request was not accepted. Reconnect.");
        }
        // Bind the originating connection generation across parsing and tool dispatch.
        // A Stop+Start between HTTP parsing and Invoke must not admit the old request.
        public static T InServerRequest<T>(long generation, Func<T> route)
        {
            lock (admissionGate) CheckAdmission(generation);
            long? previous = requestGeneration; requestGeneration = generation;
            try { return route(); }
            finally { requestGeneration = previous; }
        }
        public static JObject Invoke(Func<UIApplication, object> work, string id, string fingerprint, string tool, bool durable, int timeoutMs)
        {
            // Validate before accepting or raising; Wait must never fail after enqueueing.
            Config.ValidateTimeout(timeoutMs);
            RequestJob job;
            RequestQueue activeQueue;
            lock (admissionGate)
            {
                CheckAdmission(requestGeneration);
                activeQueue = queue;
                bool fresh;
                job = activeQueue.Accept(id, fingerprint, tool, durable, delegate { return work(currentApplication); }, out fresh);
                if (fresh)
                {
                    try
                    {
                        ExternalEventRequest raised = externalEvent.Raise();
                        if (raised == ExternalEventRequest.Denied) activeQueue.CancelQueued(id, "Revit rejected the external event before execution.");
                    }
                    catch
                    {
                        activeQueue.CancelQueued(id, "External event could not be raised; cancelled before execution.");
                        throw;
                    }
                }
            }
            // Never hold admissionGate while waiting: Stop must be able to cancel queued work.
            if (!job.Done.Wait(timeoutMs)) activeQueue.CancelQueued(id, "Wait timed out before execution. The queued operation was cancelled.");
            return activeQueue.Snapshot(job);
        }
        public static JObject Status(string id) { if (queue == null) throw new InvalidOperationException("Dispatcher not initialized."); return queue.Status(id); }
        public static JObject Cancel(string id)
        {
            if (queue == null) throw new InvalidOperationException("Dispatcher not initialized.");
            bool cancelled = queue.CancelQueued(id, "Cancelled by client before execution.");
            JObject state = queue.Status(id); state["cancelled"] = cancelled;
            if (!cancelled && (string)state["status"] == "running") state["note"] = "Already running. Arbitrary Revit/C# work cannot be forcibly interrupted safely; poll this request for its result.";
            return state;
        }
        public void Execute(UIApplication app)
        {
            currentApplication = app;
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                for (int i = 0; i < 4 && clock.ElapsedMilliseconds < 100; i++) if (!queue.ExecuteOne()) break;
            }
            finally { currentApplication = null; }
        }
        public string GetName() { return "Revit MCP guarded request dispatcher"; }
    }
}
