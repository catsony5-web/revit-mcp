using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    // UI thread only. A token is valid for one document in one Revit session.
    internal static class DocumentGuard
    {
        sealed class State { public string Token = Guid.NewGuid().ToString("N"); public long Revision; }
        static readonly Dictionary<Document, State> states = new Dictionary<Document, State>();
        static State Get(Document doc) { State state; if (!states.TryGetValue(doc, out state)) { state = new State(); states.Add(doc, state); } return state; }
        public static string Token(Document doc) { return Get(doc).Token; }
        public static long Revision(Document doc) { return Get(doc).Revision; }
        public static void Initialize(UIControlledApplication app) { app.ControlledApplication.DocumentChanged += Changed; }
        public static void Shutdown(UIControlledApplication app) { app.ControlledApplication.DocumentChanged -= Changed; states.Clear(); }
        static void Changed(object sender, DocumentChangedEventArgs args) { Document doc = args.GetDocument(); if (doc != null) Get(doc).Revision++; }
        public static JObject Describe(Document doc)
        {
            return new JObject(new JProperty("expectedDocument", Token(doc)), new JProperty("expectedRevision", Revision(doc)), new JProperty("title", doc.Title), new JProperty("path", doc.PathName), new JProperty("isReadOnly", doc.IsReadOnly), new JProperty("isWorkshared", doc.IsWorkshared));
        }
        public static object Context(UIApplication ui, JObject args)
        {
            JArray documents = new JArray();
            foreach (Document doc in ui.Application.Documents) if (!doc.IsLinked) documents.Add(Describe(doc));
            Document active = ui.ActiveUIDocument == null ? null : ui.ActiveUIDocument.Document;
            return new JObject(new JProperty("active", active == null ? null : Describe(active)), new JProperty("documents", documents), new JProperty("note", "Use active.expectedDocument and active.expectedRevision for the next write. This tool does not activate documents."));
        }
        public static void Check(UIApplication ui, JObject args, bool mutation)
        {
            string expected = A.Str(args, "expectedDocument", null);
            if (mutation && string.IsNullOrEmpty(expected)) throw new ArgumentException("expectedDocument is required. Call get_document_context first.");
            if (expected == null) return;
            Document doc = Rx.Doc(ui);
            if (!string.Equals(expected, Token(doc), StringComparison.Ordinal)) throw new InvalidOperationException("Active document changed or token expired. No operation was executed. Refresh get_document_context.");
            if (mutation && !A.Has(args, "expectedRevision")) throw new ArgumentException("expectedRevision is required. Call get_document_context first.");
            if (A.Has(args, "expectedRevision") && args["expectedRevision"].Value<long>() != Revision(doc)) throw new InvalidOperationException("Document changed since the supplied revision. Refresh context and review the plan before a new request.");
            if (mutation && doc.IsReadOnly) throw new InvalidOperationException("The target document is read-only.");
        }
    }
}
