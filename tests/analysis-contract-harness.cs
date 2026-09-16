// Offline contract tests: reads API metadata, never opens a model or calls Revit services.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp
{
    internal delegate object ToolHandler(UIApplication app, JObject args);
    internal static class ToolRegistry
    {
        internal static readonly Dictionary<string, JObject> Schemas = new Dictionary<string, JObject>();
        public static void Register(string name, string description, S schema, ToolHandler handler) { Schemas.Add(name, schema.Build()); }
    }
    internal static class Rx
    {
        public static Document Doc(UIApplication app) { throw new Exception("Live API access prohibited in contract test."); }
        public static T Tx<T>(Document doc, string name, Func<T> body) { throw new Exception("Transactions prohibited in contract test."); }
        public static JObject PtJson(XYZ p) { throw new Exception("Geometry access prohibited in contract test."); }
    }
    internal static class AnalysisContractHarness
    {
        static int assertions;
        static string apiDirectory;
        static int Main(string[] args)
        {
            apiDirectory = args[0];
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) {
                string path = Path.Combine(apiDirectory, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            try { Run(); Console.WriteLine("PASS: " + assertions + " offline analysis assertions; no model or UI access."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run()
        {
            Tools.AnalysisTools.Register();
            Check(ToolRegistry.Schemas.Count == 10, "ten tools");
            foreach (string name in new string[] { "update_energy_settings", "create_energy_model", "export_energy_gbxml", "request_systems_analysis", "cancel_systems_analysis" })
                Check((bool)ToolRegistry.Schemas[name]["properties"]["dryRun"]["default"], name + " defaults to dryRun");
            JObject patch = (JObject)ToolRegistry.Schemas["update_energy_settings"]["properties"]["patch"]["properties"];
            foreach (JProperty p in patch.Properties()) Check(p.Value["default"] == null, "patch has no implicit default: " + p.Name);
            Type type = typeof(Tools.AnalysisTools);
            Array fields = (Array)type.GetField("Settings", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Dictionary<string, object> byKey = new Dictionary<string, object>();
            foreach (object item in fields)
            {
                Type itemType = item.GetType();
                string key = (string)itemType.GetField("Key", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(item);
                PropertyInfo api = (PropertyInfo)itemType.GetProperty("Info", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(item, null);
                Check(api != null && api.CanRead && api.CanWrite, key + " exists and writable in Revit 2024 metadata");
                byKey.Add(key, item);
            }
            CheckClose(Convert(type, byKey["coreOffsetMm"], new JValue(3048.0)), 10.0, "mm->feet");
            CheckClose(Convert(type, byKey["glazingPercent"], new JValue(40.0)), 0.4, "percent->ratio");
            CheckClose(Convert(type, byKey["outsideAirLitresPerSecondPerPerson"], new JValue(28.316846592 / 3600)), 1, "L/s/person->ft3/hour");
            CheckClose(Convert(type, byKey["outsideAirLitresPerSecondPerM2"], new JValue(28.316846592 / 3600 / 0.09290304)), 1, "L/s/m2->ft3/hour/ft2");
            Reject(delegate { Convert(type, byKey["glazingPercent"], new JValue(95.1)); }, "glazing upper bound");
            Reject(delegate { Convert(type, byKey["glazingPercent"], new JValue(-1)); }, "glazing lower bound");
            Reject(delegate { Convert(type, byKey["analyticalGridCellSizeMm"], new JValue(0)); }, "positive grid size");
            Reject(delegate { Convert(type, byKey["skylightWidthMm"], new JValue(100)); }, "minimum skylight width");
            Reject(delegate { Convert(type, byKey["outsideAirChangesPerHour"], new JValue(double.NaN)); }, "finite numbers only");
            Reject(delegate { Convert(type, byKey["dividePerimeter"], new JValue("false")); }, "strict bool");
            foreach (string path in new string[] { "relative.xml", "C:relative.xml", "\\rooted.xml", "\\\\server\\share\\a.xml", "C:\\folder\\a.xml:stream" })
                Reject(delegate { Invoke(type, "LocalPath", path, "outputPath"); }, "reject unsafe or ambiguous path: " + path);
            Check((string)Invoke(type, "LocalPath", "C:\\Reports\\new.xml", "outputPath") == "C:\\Reports\\new.xml", "absolute local path accepted");
            Reject(delegate { Invoke(type, "Flag", new JObject(new JProperty("dryRun", "false")), "dryRun", true); }, "malformed dryRun rejected");
            Reject(delegate { Invoke(type, "EnumValue", typeof(Autodesk.Revit.DB.Analysis.EnergyModelType), "999", "modelType"); }, "numeric enum bypass rejected");
        }
        static double Convert(Type type, object field, JToken value) { return (double)Invoke(type, "ConvertSetting", field, value, null); }
        static object Invoke(Type type, string name, params object[] args) { return type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args); }
        static void Reject(Action action, string label)
        {
            try { action(); } catch (TargetInvocationException ex) { if (ex.InnerException is ArgumentException) { assertions++; return; } throw; }
            throw new Exception("Expected rejection: " + label);
        }
        static void CheckClose(double actual, double expected, string label) { Check(Math.Abs(actual - expected) < 1e-9, label); }
        static void Check(bool result, string label) { if (!result) throw new Exception("Assertion failed: " + label); assertions++; }
    }
}
