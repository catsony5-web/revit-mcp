// Offline harness: real server/dispatcher/queue/config, tiny Revit boundary stubs.
// No Revit assemblies are loaded and no model, external event or UI is accessed.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RevitMcp;

namespace Autodesk.Revit.UI.Events { public class IdlingEventArgs : EventArgs { } }
namespace Autodesk.Revit.UI
{
    public class UIApplication { }
    public interface IExternalEventHandler { void Execute(UIApplication app); string GetName(); }
    public enum ExternalEventRequest { Accepted, Pending, Denied }
    public class UIControlledApplication
    {
        public event EventHandler<Events.IdlingEventArgs> Idling;
        public void SimulateIdle() { if (Idling != null) Idling(this, new Events.IdlingEventArgs()); }
    }
    public sealed class ExternalEvent : IDisposable
    {
        public static IExternalEventHandler Handler;
        public static readonly ManualResetEventSlim Raised = new ManualResetEventSlim(false);
        public static ExternalEvent Create(IExternalEventHandler handler) { Handler = handler; return new ExternalEvent(); }
        public ExternalEventRequest Raise() { Raised.Set(); return ExternalEventRequest.Accepted; }
        public void Dispose() { }
    }
}
namespace RevitMcp
{
    internal static class DataStore { public static string Root; }
    internal static class DocumentGuard
    {
        public static void Initialize(Autodesk.Revit.UI.UIControlledApplication app) { }
        public static void Shutdown(Autodesk.Revit.UI.UIControlledApplication app) { }
    }
    internal static class Log { public static void Info(string message) { } public static void Error(string message, Exception error) { } }
    internal static class ToolRegistry { public static int Count { get { return 1; } } }
    internal static class McpProtocol
    {
        public static int Effects, Routed;
        public static readonly ManualResetEventSlim StaleEntered = new ManualResetEventSlim(false), StaleRelease = new ManualResetEventSlim(false), StaleFinished = new ManualResetEventSlim(false);
        public static string Handle(string body)
        {
            Interlocked.Increment(ref Routed);
            try
            {
                if (body == "stale") { StaleEntered.Set(); if (!StaleRelease.Wait(5000)) throw new Exception("test barrier timeout"); }
                return Dispatcher.Invoke(delegate(Autodesk.Revit.UI.UIApplication ui) { Interlocked.Increment(ref Effects); return body; }, body, body, "test", true, 5000).ToString();
            }
            catch (Exception ex) { return new JObject(new JProperty("rejected", ex.Message)).ToString(); }
            finally { if (body == "stale") StaleFinished.Set(); }
        }
    }
}
static class ServerLifecycleTests
{
    static int assertions;
    static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    static void Throws(Action action, string message) { bool thrown = false; try { action(); } catch (Exception) { thrown = true; } Check(thrown, message); }
    static int Port()
    {
        TcpListener probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); return port;
    }
    static TcpClient Post(int port, string body)
    {
        TcpClient client = new TcpClient(); client.ReceiveTimeout = 5000; client.SendTimeout = 5000; client.Connect(IPAddress.Loopback, port);
        byte[] bytes = Encoding.ASCII.GetBytes("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n" + body);
        client.GetStream().Write(bytes, 0, bytes.Length); return client;
    }
    static void RunQueued() { Autodesk.Revit.UI.ExternalEvent.Handler.Execute(new Autodesk.Revit.UI.UIApplication()); }
    static string State(string id) { return (string)Dispatcher.Status(id)["status"]; }
    static void Main(string[] args)
    {
        DataStore.Root = args[0];
        Config.Apply(JObject.Parse("{\"port\":8090,\"defaultTimeoutMs\":1000,\"maxTimeoutMs\":10000,\"autoStart\":false}"));
        Throws(delegate { Config.Apply(JObject.Parse("{\"port\":9999,\"defaultTimeoutMs\":-1}")); }, "negative config timeout rejected");
        Check(Config.Port == 8090 && Config.DefaultTimeoutMs == 1000 && !Config.AutoStart, "invalid config does not partially update fields");
        Throws(delegate { Config.Apply(JObject.Parse("{\"maxTimeoutMs\":600001}")); }, "configuration cannot exceed hard timeout limit");
        Throws(delegate { Config.Apply(JObject.Parse("{\"defaultTimeoutMs\":10001}")); }, "default timeout must fit configured maximum");
        Throws(delegate { Config.Apply(JObject.Parse("{\"port\":65536}")); }, "invalid port rejected");
        Throws(delegate { Config.Apply(JObject.Parse("{\"defaultTimeoutMs\":\"2000\"}")); }, "numeric strings rejected in configuration");
        Throws(delegate { Config.ClampTimeout(0); }, "zero requested wait rejected");
        Throws(delegate { Config.ClampTimeout(-1); }, "infinite requested wait rejected");
        Check(Config.ClampTimeout(20000) == 10000 && Config.ClampTimeout(null) == 1000, "valid waits default and clamp predictably");
        Autodesk.Revit.UI.UIControlledApplication app = new Autodesk.Revit.UI.UIControlledApplication();
        Dispatcher.Initialize(app);
        Throws(delegate { Dispatcher.Invoke(delegate { return 0; }, "before-start", "x", "test", true, 1000); }, "dispatcher starts with admission paused");
        HttpServer server = new HttpServer(); int port = Port(); server.Start(port);
        Throws(delegate { Dispatcher.Invoke(delegate { return 0; }, "bad-wait", "x", "test", true, -2); }, "bad wait rejected before queue admission");
        Check(State("bad-wait") == "not_found", "bad wait leaves no queued job or durable journal");
        Throws(delegate { Dispatcher.Invoke(delegate { return 0; }, "huge-wait", "x", "test", true, 600001); }, "oversized direct wait rejected before admission");
        Check(State("huge-wait") == "not_found", "oversized wait leaves no queued job");

        using (TcpClient stale = Post(port, "stale"))
        {
            Check(McpProtocol.StaleEntered.Wait(5000), "HTTP request reaches delayed pre-dispatch boundary");
            server.Stop(); server.Start(port); McpProtocol.StaleRelease.Set();
            Check(McpProtocol.StaleFinished.Wait(5000), "stale connection route exits after restart");
            RunQueued();
            Check(State("stale") == "not_found" && McpProtocol.Effects == 0, "old route cannot enqueue after Stop and Start");
        }
        Autodesk.Revit.UI.ExternalEvent.Raised.Reset();
        using (TcpClient queued = Post(port, "cancel-on-stop"))
        {
            Check(Autodesk.Revit.UI.ExternalEvent.Raised.Wait(5000), "request is queued before stop");
            server.Stop(); RunQueued();
            Check(State("cancel-on-stop") == "cancelled_before_execution" && McpProtocol.Effects == 0, "Stop cancels queued work before simulated Revit execution");
        }
        Throws(delegate { Dispatcher.Invoke(delegate { return 0; }, "while-stopped", "x", "test", true, 1000); }, "stopped dispatcher rejects fresh admission");
        Check(State("while-stopped") == "not_found", "stopped admission does not create a job");

        server.Start(port); Autodesk.Revit.UI.ExternalEvent.Raised.Reset();
        using (TcpClient fresh = Post(port, "fresh-after-restart"))
        {
            Check(Autodesk.Revit.UI.ExternalEvent.Raised.Wait(5000), "new server generation accepts requests"); RunQueued();
            string response = new StreamReader(fresh.GetStream()).ReadToEnd();
            Check(response.Contains("succeeded") && response.Contains("Connection: close"), "new request returns committed response and honors close");
            Check(State("fresh-after-restart") == "succeeded" && McpProtocol.Effects == 1, "only the new request executes once");
        }
        server.Stop(); Dispatcher.Shutdown(app);
        Console.WriteLine(assertions + " offline server lifecycle/configuration assertions passed.");
    }
}
