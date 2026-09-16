// Policy tests compile the REAL DocumentGuard, Schema and Units against tiny fake Revit types.
// No RevitAPI assembly is referenced; no model, application, window or native event is accessed.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RevitMcp;

namespace Autodesk.Revit.DB
{
    // Reference identity is deliberate: same metadata must never make two document objects interchangeable.
    public sealed class Document
    {
        public string Title { get; set; }
        public string PathName { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsWorkshared { get; set; }
        public bool IsLinked { get; set; }
    }
    // Schema/Units use these types; no geometry test is implied.
    public sealed class ElementId { public long Value; public ElementId(int value) { Value = value; } public ElementId(long value) { Value = value; } }
    public sealed class XYZ { public double X, Y, Z; public XYZ(double x, double y, double z) { X = x; Y = y; Z = z; } }
}
namespace Autodesk.Revit.DB.Events
{
    public sealed class DocumentChangedEventArgs : EventArgs
    {
        readonly Document document;
        public DocumentChangedEventArgs(Document doc) { document = doc; }
        public Document GetDocument() { return document; }
    }
}
namespace Autodesk.Revit.ApplicationServices
{
    public sealed class Application { public readonly IList<Document> Documents = new List<Document>(); }
    public sealed class ControlledApplication
    {
        EventHandler<DocumentChangedEventArgs> handlers;
        public event EventHandler<DocumentChangedEventArgs> DocumentChanged
        {
            add { handlers += value; }
            remove { handlers -= value; }
        }
        public int ListenerCount { get { return handlers == null ? 0 : handlers.GetInvocationList().Length; } }
        public void RaiseDocumentChanged(Document doc)
        {
            EventHandler<DocumentChangedEventArgs> snapshot = handlers;
            if (snapshot != null) snapshot(this, new DocumentChangedEventArgs(doc));
        }
    }
}
namespace Autodesk.Revit.UI
{
    public sealed class UIDocument { public Document Document { get; set; } }
    public sealed class UIApplication
    {
        public Autodesk.Revit.ApplicationServices.Application Application { get; set; }
        public UIDocument ActiveUIDocument { get; set; }
    }
    public sealed class UIControlledApplication
    {
        public Autodesk.Revit.ApplicationServices.ControlledApplication ControlledApplication { get; set; }
    }
}
namespace RevitMcp
{
    // Mirrors the production document accessor. The real argument helpers come from src/Schema.cs.
    internal static class Rx
    {
        public static Document Doc(UIApplication ui)
        {
            UIDocument active = ui == null ? null : ui.ActiveUIDocument;
            if (active == null || active.Document == null) throw new InvalidOperationException("No active document.");
            return active.Document;
        }
    }
}

internal static class DocumentGuardTests
{
    static int checks;
    static void Check(bool pass, string label)
    {
        if (!pass) throw new Exception("FAIL: " + label);
        checks++; Console.WriteLine("PASS: " + label);
    }
    static void Reject<T>(Action action, string messagePart, string label) where T : Exception
    {
        try { action(); }
        catch (T ex) { Check(ex.Message.IndexOf(messagePart, StringComparison.OrdinalIgnoreCase) >= 0, label); return; }
        throw new Exception("FAIL: expected " + typeof(T).Name + " for " + label);
    }
    static JObject Expected(Document doc)
    {
        return new JObject(new JProperty("expectedDocument", DocumentGuard.Token(doc)), new JProperty("expectedRevision", DocumentGuard.Revision(doc)));
    }
    static void Allowed(UIApplication ui, JObject args, bool mutation, string label) { DocumentGuard.Check(ui, args, mutation); Check(true, label); }

    static int Main()
    {
        try { Run(); Console.WriteLine(checks + " DocumentGuard policy checks passed using fakes. Real Revit event delivery and native document identity remain untested."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Run()
    {
        Autodesk.Revit.ApplicationServices.ControlledApplication events = new Autodesk.Revit.ApplicationServices.ControlledApplication();
        UIControlledApplication controller = new UIControlledApplication { ControlledApplication = events };
        Autodesk.Revit.ApplicationServices.Application application = new Autodesk.Revit.ApplicationServices.Application();
        Document first = new Document { Title = "Same title", PathName = @"C:\Models\same.rvt", IsWorkshared = true };
        Document second = new Document { Title = first.Title, PathName = first.PathName };
        Document linked = new Document { Title = "Linked", PathName = @"C:\Models\linked.rvt", IsLinked = true };
        application.Documents.Add(first); application.Documents.Add(second); application.Documents.Add(linked);
        UIApplication ui = new UIApplication { Application = application, ActiveUIDocument = new UIDocument { Document = first } };
        DocumentGuard.Initialize(controller);
        Check(events.ListenerCount == 1, "initialization subscribes one change listener");

        string firstToken = DocumentGuard.Token(first), secondToken = DocumentGuard.Token(second);
        Check(firstToken != secondToken, "separate documents with identical title/path receive distinct tokens");
        Check(firstToken == DocumentGuard.Token(first), "same document has a stable session token");
        Check(DocumentGuard.Revision(first) == 0 && DocumentGuard.Revision(second) == 0, "new documents start at revision zero");
        JObject context = (JObject)DocumentGuard.Context(ui, new JObject());
        Check((string)context["active"]["expectedDocument"] == firstToken, "context identifies the active document by token");
        Check(((JArray)context["documents"]).Count == 2, "context excludes linked documents");
        Check((string)context["active"]["title"] == first.Title && (string)context["active"]["path"] == first.PathName, "context preserves display metadata");
        Check((bool)context["active"]["isWorkshared"] && !(bool)context["active"]["isReadOnly"], "context exposes document restrictions");
        Allowed(ui, Expected(first), true, "matching token and revision permit mutation");

        Reject<ArgumentException>(delegate { DocumentGuard.Check(ui, new JObject(), true); }, "expectedDocument", "mutation requires document token");
        Reject<ArgumentException>(delegate { DocumentGuard.Check(ui, new JObject(new JProperty("expectedDocument", "")), true); }, "expectedDocument", "mutation rejects empty document token");
        Reject<ArgumentException>(delegate { DocumentGuard.Check(ui, new JObject(new JProperty("expectedDocument", JValue.CreateNull())), true); }, "expectedDocument", "mutation rejects null document token");
        Reject<ArgumentException>(delegate { DocumentGuard.Check(ui, new JObject(new JProperty("expectedDocument", firstToken)), true); }, "expectedRevision", "mutation requires revision");
        Reject<ArgumentException>(delegate { DocumentGuard.Check(ui, new JObject(new JProperty("expectedDocument", firstToken), new JProperty("expectedRevision", JValue.CreateNull())), true); }, "expectedRevision", "mutation rejects null revision");

        JObject beforeSwitch = Expected(first);
        ui.ActiveUIDocument.Document = second;
        Reject<InvalidOperationException>(delegate { DocumentGuard.Check(ui, beforeSwitch, true); }, "Active document", "active-document switch blocks the queued mutation");
        Reject<InvalidOperationException>(delegate { DocumentGuard.Check(ui, beforeSwitch, false); }, "Active document", "explicitly guarded read also rejects an active-document switch");
        Allowed(ui, Expected(second), true, "newly acquired context permits intended second document");
        ui.ActiveUIDocument.Document = first;
        Allowed(ui, beforeSwitch, true, "returning to unchanged original document keeps its token valid");

        JObject beforeEdit = Expected(first);
        events.RaiseDocumentChanged(first);
        Check(DocumentGuard.Revision(first) == 1, "DocumentChanged advances its document revision");
        Check(DocumentGuard.Revision(second) == 0, "document change does not alter another document revision");
        Reject<InvalidOperationException>(delegate { DocumentGuard.Check(ui, beforeEdit, true); }, "Document changed", "stale revision blocks a mutation");
        Reject<InvalidOperationException>(delegate { DocumentGuard.Check(ui, beforeEdit, false); }, "Document changed", "supplied stale revision also blocks a read");
        Allowed(ui, Expected(first), true, "fresh revision allows reviewed mutation");
        events.RaiseDocumentChanged(first);
        Check(DocumentGuard.Revision(first) == 2, "each change notification advances revision exactly once");
        events.RaiseDocumentChanged(null);
        Check(DocumentGuard.Revision(first) == 2, "null-document event leaves revisions unchanged");
        Document unseen = new Document { Title = "Previously unseen", PathName = "" };
        events.RaiseDocumentChanged(unseen);
        Check(DocumentGuard.Revision(unseen) == 1, "change event tracks a document before its first context lookup");

        first.IsReadOnly = true;
        Reject<InvalidOperationException>(delegate { DocumentGuard.Check(ui, Expected(first), true); }, "read-only", "read-only document rejects mutation with otherwise valid context");
        Allowed(ui, Expected(first), false, "read-only document permits reads");
        first.IsReadOnly = false;
        Allowed(ui, new JObject(), false, "read may omit document guard");
        Allowed(ui, new JObject(new JProperty("expectedDocument", firstToken)), false, "read may supply a token without revision");

        ui.ActiveUIDocument = null;
        context = (JObject)DocumentGuard.Context(ui, new JObject());
        Check(context["active"].Type == JTokenType.Null && ((JArray)context["documents"]).Count == 2, "context handles no active document without activating one");
        Reject<InvalidOperationException>(delegate { DocumentGuard.Check(ui, Expected(first), true); }, "active document", "mutation cannot run with no active document");
        Allowed(ui, new JObject(), false, "unguarded context read remains possible with no active document");
        ui.ActiveUIDocument = new UIDocument { Document = first };

        string oldToken = DocumentGuard.Token(first);
        JObject oldContext = Expected(first);
        DocumentGuard.Shutdown(controller);
        Check(events.ListenerCount == 0, "shutdown detaches its change listener");
        events.RaiseDocumentChanged(first);
        Check(DocumentGuard.Revision(first) == 0, "shutdown clears revisions and detached events cannot increment them");
        Check(DocumentGuard.Token(first) != oldToken, "shutdown invalidates old session tokens even for the same object");
        Reject<InvalidOperationException>(delegate { DocumentGuard.Check(ui, oldContext, true); }, "token expired", "previous-session context cannot authorize a mutation");
        DocumentGuard.Initialize(controller);
        Check(events.ListenerCount == 1, "reinitialization has no duplicate event listener");
        events.RaiseDocumentChanged(first);
        Check(DocumentGuard.Revision(first) == 1, "change notification works once after reinitialization");
        DocumentGuard.Shutdown(controller);
        Check(events.ListenerCount == 0, "test cleanup leaves no listener registered");
    }
}
