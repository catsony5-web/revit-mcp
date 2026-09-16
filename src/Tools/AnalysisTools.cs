using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Tools
{
    // Revit 2024 API surface only. No UI command posting or automatic cloud submission.
    internal static class AnalysisTools
    {
        const double Ft2ToM2 = 0.09290304;
        const double Ft3ToM3 = 0.028316846592;

        sealed class Setting
        {
            internal string Key, Property, Unit;
            internal double Scale = 1, Minimum = double.NaN, Maximum = double.NaN;
            internal Setting(string key, string property, string unit, double scale, double min, double max)
            { Key = key; Property = property; Unit = unit; Scale = scale; Minimum = min; Maximum = max; }
            internal PropertyInfo Info { get { return typeof(EnergyDataSettings).GetProperty(Property); } }
        }

        static Setting Simple(string key, string property) { return new Setting(key, property, null, 1, double.NaN, double.NaN); }
        static Setting Number(string key, string property, string unit, double scale, double min, double max)
        { return new Setting(key, property, unit, scale, min, max); }

        // Numeric Scale converts API values to published units. Property names are never supplied by callers.
        static readonly Setting[] Settings = new Setting[] {
            Simple("analysisType", "AnalysisType"), Simple("buildingType", "BuildingType"),
            Simple("buildingHVACSystem", "BuildingHVACSystem"), Simple("buildingOperatingSchedule", "BuildingOperatingSchedule"),
            Simple("buildingEnvelopeDeterminationMethod", "BuildingEnvelopeDeterminationMethod"),
            Simple("exportComplexity", "ExportComplexity"), Simple("serviceType", "ServiceType"),
            Simple("buildingConstructionClass", "BuildingConstructionClass"), Simple("projectReportType", "ProjectReportType"),
            Simple("dividePerimeter", "DividePerimeter"), Simple("includeThermalProperties", "IncludeThermalProperties"),
            Simple("useCurrentViewOnly", "UseCurrentViewOnly"), Simple("exportDefaults", "ExportDefaults"),
            Simple("useHeatingCredits", "UseHeatingCredits"), Simple("isGlazingShaded", "IsGlazingShaded"),
            Simple("useAirChangesPerHour", "UseAirChangesPerHour"), Simple("useOutsideAirPerArea", "UseOutsideAirPerArea"),
            Simple("useOutsideAirPerPerson", "UseOutsideAirPerPerson"),
            Simple("projectPhaseId", "ProjectPhase"), Simple("groundPlaneId", "GroundPlane"),
            Number("coreOffsetMm", "CoreOffset", "mm", 304.8, 0, 9144000),
            Number("analyticalGridCellSizeMm", "AnalyticalGridCellSize", "mm", 304.8, 0.001, double.NaN),
            Number("sliverSpaceToleranceMm", "SliverSpaceTolerance", "mm", 304.8, 0, double.NaN),
            Number("skylightWidthMm", "SkylightWidth", "mm", 304.8, 203.2, double.NaN),
            Number("shadeDepthMm", "ShadeDepth", "mm", 304.8, 0, double.NaN),
            Number("sillHeightMm", "SillHeight", "mm", 304.8, 0, double.NaN),
            Number("glazingPercent", "PercentageGlazing", "% (0-95)", 100, 0, 95),
            Number("skylightsPercent", "PercentageSkylights", "% (0-95)", 100, 0, 95),
            Number("outsideAirChangesPerHour", "OutsideAirChangesRatePerHour", "1/h", 1, 0, double.NaN),
            // API stores ft3/hour and ft3/hour/ft2, not ft3/second.
            Number("outsideAirLitresPerSecondPerPerson", "OutsideAirPerPerson", "L/s/person", 28.316846592 / 3600, 0, double.NaN),
            Number("outsideAirLitresPerSecondPerM2", "OutsideAirPerArea", "L/s/m2", 28.316846592 / 3600 / Ft2ToM2, 0, double.NaN)
        };

        static S PatchSchema()
        {
            S result = S.Obj();
            foreach (Setting s in Settings)
            {
                Type type = s.Info.PropertyType;
                if (type.IsEnum) result.Enum(s.Key, s.Property, Enum.GetNames(type), null);
                else if (type == typeof(bool)) result.Bool(s.Key, s.Property, false);
                else if (type == typeof(ElementId)) result.Int(s.Key, "ElementId in the guarded document. groundPlaneId accepts -1.");
                else result.Num(s.Key, s.Property + " [" + s.Unit + "]");
            }
            result.Enum("exportCategory", "Spatial elements used for export", new string[] { "Rooms", "Spaces" }, null);
            // A patch has no implicit values: schema defaults must not invite clients to reset omitted booleans.
            foreach (JProperty field in ((JObject)result.Build()["properties"]).Properties()) ((JObject)field.Value).Remove("default");
            return result;
        }

        public static void Register()
        {
            ToolRegistry.Register("get_analysis_capabilities", "Lists implemented Revit 2024 analysis workflows and known limits. Does not claim all Analyze-tab commands are available.", S.Obj(), Capabilities);
            ToolRegistry.Register("get_energy_settings", "Reads energy settings, units and writable enum values; reports read failures explicitly.", S.Obj(), GetSettings);
            ToolRegistry.Register("update_energy_settings", "Validates and patches an allowlist of energy settings atomically. Existing energy model is not rebuilt. dryRun defaults to true.",
                S.Obj().Sub("patch", "Only listed fields can be changed; omitted fields stay unchanged.", PatchSchema(), true).Bool("dryRun", "Return changes without a transaction", true), UpdateSettings);
            ToolRegistry.Register("get_energy_model", "Reads the existing main energy model with original-element references and optional surface polygons in mm. Does not create a model.",
                S.Obj().IntDef("limit", "Maximum spaces and maximum surfaces returned, each 1-1000", 100).Bool("includeGeometry", "Include surface polygons, capped at 20000 points", false), GetModel);
            ToolRegistry.Register("get_spatial_energy_diagnostics", "Checks rooms/spaces for placement, positive area and volume. This is a preliminary diagnostic, not an envelope watertightness or simulation validation.",
                S.Obj().IntDef("limit", "Maximum issues returned, 1-2000", 200), SpatialDiagnostics);
            ToolRegistry.Register("create_energy_model", "Creates a detailed energy model. Existing main model replacement requires replaceExisting=true and runs in one rollback-capable transaction. dryRun defaults to true.",
                S.Obj().Enum("modelType", "Input geometry", new string[] { "SpatialElement", "BuildingElement", "AnalysisMode" }, "AnalysisMode")
                    .Enum("tier", "Computed detail", new string[] { "FirstLevelBoundaries", "SecondLevelBoundaries", "Final" }, "Final")
                    .Bool("replaceExisting", "Explicitly delete existing main energy model before recreation", false)
                    .Bool("includeShadingSurfaces", "Include shading", true).Bool("exportMullions", "Export mullions as shading", false)
                    .Bool("simplifyCurtainSystems", "Combine curtain openings", true).Int("expectedViewId", "Required if energy settings use the current 3D view")
                    .Bool("dryRun", "Return plan without model changes", true), CreateModel);
            ToolRegistry.Register("export_energy_gbxml", "Exports the existing compatible main energy model to a NEW local .xml file. Does not overwrite or create energy models. dryRun defaults to true.",
                S.Obj().StrReq("outputPath", "Absolute path of a new .xml file in an existing folder")
                    .Enum("modelType", "Must match the existing model", new string[] { "AnalysisMode", "SpatialElement", "BuildingElement" }, "AnalysisMode")
                    .Bool("exportAnalyticalSystems", "Include analytical systems", true).Bool("dryRun", "Validate path and report planned export", true), ExportGbxml);
            ToolRegistry.Register("request_systems_analysis", "Creates a systems analysis report view then requests background analysis with explicit local .epw and .osw files. Returns reportId; poll status separately. dryRun defaults to true.",
                S.Obj().StrReq("reportName", "Unique report view name").StrReq("weatherFile", "Existing absolute local EnergyPlus .epw file")
                    .StrReq("workflowFile", "Existing absolute local OpenStudio .osw workflow; it can execute workflow measures")
                    .StrReq("outputFolder", "Existing empty local folder dedicated to this analysis")
                    .Bool("dryRun", "Return plan without creating a report or starting an analysis", true), RequestSystems);
            ToolRegistry.Register("get_systems_analysis_status", "Gets report completion state and optional bounded report content. completed means the calculation ended, not that it succeeded.",
                S.Obj().Int("reportId", "Specific report id; omitted uses latest report")
                    .Bool("includeContent", "Return bounded report text or referenced file path", false).IntDef("maxContentChars", "Maximum characters, 1-100000", 20000), SystemsStatus);
            ToolRegistry.Register("cancel_systems_analysis", "Requests cancellation for one explicit systems-analysis report. It does not delete the view/files or guarantee immediate termination.",
                S.Obj().IntReq("reportId", "Report belonging to guarded document").Bool("dryRun", "Return plan without cancellation", true), CancelSystems);
        }

        static object Capabilities(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            return new JObject(new JProperty("revitVersion", uiapp.Application.VersionNumber), new JProperty("documentTitle", doc.Title),
                new JProperty("implemented", new JArray("energy settings read/patch", "room/space preliminary diagnostics", "detailed energy model create/rebuild/inspect", "gbXML export", "systems analysis request/status/cancel")),
                new JProperty("notImplemented", new JArray("all Analyze-tab UI commands", "cloud Insight submission", "structural analysis solvers", "solar/daylight simulation", "free editing of generated analytical surface geometry", "automatic original model envelope repair")),
                new JProperty("requirements", new JArray("Revit 2024 project document", "valid source geometry for model creation", "installed systems-analysis engine and local weather/workflow files for simulation", "requestId + expectedDocument + expectedRevision for mutations")),
                new JProperty("validation", "Compiled against Revit 2024 API; runtime project validation is required before production use."));
        }

        static EnergyDataSettings Data(Document doc)
        {
            if (doc.IsFamilyDocument) throw new InvalidOperationException("Analysis requires a project document, not a family document.");
            EnergyDataSettings data = EnergyDataSettings.GetFromDocument(doc);
            if (data == null) throw new InvalidOperationException("No EnergyDataSettings in this document.");
            return data;
        }

        static JToken Published(Setting s, object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is ElementId) return new JValue(((ElementId)value).Value);
            if (value.GetType().IsEnum) return new JValue(value.ToString());
            if (value is double) return new JValue((double)value * s.Scale);
            return JToken.FromObject(value);
        }

        static JObject ReadSettings(Document doc)
        {
            EnergyDataSettings data = Data(doc);
            JObject values = new JObject(), supported = new JObject();
            JArray errors = new JArray();
            foreach (Setting s in Settings)
            {
                PropertyInfo info = s.Info;
                JObject definition = new JObject(new JProperty("apiProperty", s.Property), new JProperty("writable", info != null && info.CanWrite));
                if (s.Unit != null) definition["unit"] = s.Unit;
                if (!double.IsNaN(s.Minimum)) definition["minimum"] = s.Minimum;
                if (!double.IsNaN(s.Maximum)) definition["maximum"] = s.Maximum;
                if (info != null && info.PropertyType.IsEnum) definition["values"] = new JArray(Enum.GetNames(info.PropertyType));
                supported[s.Key] = definition;
                try { values[s.Key] = Published(s, info.GetValue(data, null)); }
                catch (Exception ex) { errors.Add(Error(s.Key, ex)); }
            }
            values["exportCategory"] = data.ExportCategory.Value == (long)BuiltInCategory.OST_Rooms ? "Rooms" : "Spaces";
            supported["exportCategory"] = new JObject(new JProperty("writable", true), new JProperty("values", new JArray("Rooms", "Spaces")));
            return new JObject(new JProperty("documentTitle", doc.Title), new JProperty("settingsElementId", data.Id.Value),
                new JProperty("settings", values), new JProperty("supportedFields", supported), new JProperty("readErrors", errors),
                new JProperty("note", "Settings changes do not regenerate an existing energy model. Preview and rebuild separately."));
        }

        static object GetSettings(UIApplication uiapp, JObject args) { return ReadSettings(Rx.Doc(uiapp)); }

        static object UpdateSettings(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            EnergyDataSettings data = Data(doc);
            JObject patch = args["patch"] as JObject;
            if (patch == null || !patch.Properties().Any()) throw new ArgumentException("patch must be a nonempty object.");
            Dictionary<Setting, object> converted = new Dictionary<Setting, object>();
            ElementId exportCategory = null;
            JArray changes = new JArray();
            foreach (JProperty p in patch.Properties())
            {
                if (p.Name == "exportCategory")
                {
                    string choice = Text(p.Value, p.Name);
                    if (choice != "Rooms" && choice != "Spaces") throw new ArgumentException("exportCategory must be Rooms or Spaces.");
                    exportCategory = new ElementId(choice == "Rooms" ? BuiltInCategory.OST_Rooms : BuiltInCategory.OST_MEPSpaces);
                    changes.Add(new JObject(new JProperty("field", p.Name), new JProperty("beforeElementId", data.ExportCategory.Value), new JProperty("after", choice)));
                    continue;
                }
                Setting s = Settings.FirstOrDefault(delegate(Setting x) { return x.Key == p.Name; });
                if (s == null || !s.Info.CanWrite) throw new ArgumentException("Unknown or non-writable energy setting: " + p.Name);
                object value = ConvertSetting(s, p.Value, doc);
                converted.Add(s, value);
                changes.Add(new JObject(new JProperty("field", s.Key), new JProperty("before", Published(s, s.Info.GetValue(data, null))), new JProperty("after", Published(s, value))));
            }
            JObject result = new JObject(new JProperty("dryRun", Flag(args, "dryRun", true)), new JProperty("changes", changes), new JProperty("energyModelRebuilt", false));
            if ((bool)result["dryRun"]) return result;
            Rx.Tx(doc, "MCP: Update energy settings", delegate {
                foreach (KeyValuePair<Setting, object> p in converted)
                {
                    try { p.Key.Info.SetValue(data, p.Value, null); }
                    catch (TargetInvocationException ex) { throw new InvalidOperationException("Energy setting rejected by Revit: " + p.Key.Key, ex.InnerException ?? ex); }
                }
                if (exportCategory != null) data.ExportCategory = exportCategory;
                return true;
            });
            result["applied"] = true;
            result["after"] = ReadSettings(doc);
            return result;
        }

        static object ConvertSetting(Setting s, JToken token, Document doc)
        {
            Type type = s.Info.PropertyType;
            if (type.IsEnum) return EnumValue(type, Text(token, s.Key), s.Key);
            if (type == typeof(bool)) { if (token.Type != JTokenType.Boolean) throw new ArgumentException(s.Key + " must be boolean."); return token.Value<bool>(); }
            if (type == typeof(ElementId))
            {
                if (token.Type != JTokenType.Integer) throw new ArgumentException(s.Key + " must be an integer ElementId.");
                ElementId id = new ElementId(token.Value<long>());
                if (s.Key == "groundPlaneId" && id == ElementId.InvalidElementId) return id;
                Element e = doc.GetElement(id);
                if (s.Key == "groundPlaneId" ? !(e is Level) : !(e is Phase)) throw new ArgumentException(s.Key + " does not identify the required element type in this document.");
                return id;
            }
            double n = Numeric(token, s.Key);
            if ((!double.IsNaN(s.Minimum) && n < s.Minimum) || (!double.IsNaN(s.Maximum) && n > s.Maximum))
                throw new ArgumentOutOfRangeException(s.Key, "Value is outside the documented range. See get_energy_settings.supportedFields.");
            return n / s.Scale;
        }

        static object GetModel(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            Data(doc);
            int limit = Limit(args, "limit", 100, 1000);
            bool geometry = Flag(args, "includeGeometry", false);
            EnergyAnalysisDetailModel model = EnergyAnalysisDetailModel.GetMainEnergyAnalysisDetailModel(doc);
            JObject result = new JObject(new JProperty("exists", model != null), new JProperty("documentTitle", doc.Title));
            if (model == null) return result;
            IList<EnergyAnalysisSpace> spaces = model.GetAnalyticalSpaces();
            IList<EnergyAnalysisSurface> surfaces = model.GetAnalyticalSurfaces();
            JArray spaceRows = new JArray(), surfaceRows = new JArray(), errors = new JArray();
            foreach (EnergyAnalysisSpace s in spaces.Take(limit))
            {
                try { spaceRows.Add(new JObject(new JProperty("id", s.Id.Value), new JProperty("name", s.SpaceName), new JProperty("number", s.Number), new JProperty("sourceUniqueId", s.CADObjectUniqueId), new JProperty("areaM2", s.Area * Ft2ToM2), new JProperty("volumeM3", s.Volume * Ft3ToM3))); }
                catch (Exception ex) { errors.Add(Error("space " + s.Id.Value, ex)); }
            }
            int remainingPoints = 20000;
            bool geometryTruncated = false;
            foreach (EnergyAnalysisSurface s in surfaces.Take(limit))
            {
                try
                {
                    JObject row = new JObject(new JProperty("id", s.Id.Value), new JProperty("name", s.SurfaceName), new JProperty("type", s.Type.ToString()),
                        new JProperty("sourceUniqueId", s.CADObjectUniqueId), new JProperty("sourceLinkUniqueId", s.CADLinkUniqueId), new JProperty("sourceDescription", s.OriginatingElementDescription));
                    EnergyAnalysisSpace first = s.GetAnalyticalSpace(), second = s.GetAdjacentAnalyticalSpace();
                    row["spaceId"] = first == null ? JValue.CreateNull() : new JValue(first.Id.Value);
                    row["adjacentSpaceId"] = second == null ? JValue.CreateNull() : new JValue(second.Id.Value);
                    if (geometry)
                    {
                        JArray loops = new JArray();
                        foreach (Polyloop loop in s.GetPolyloops())
                        {
                            IList<XYZ> points = loop.GetPoints();
                            if (points.Count > remainingPoints) { geometryTruncated = true; break; }
                            JArray polygon = new JArray();
                            foreach (XYZ point in points) polygon.Add(Rx.PtJson(point));
                            loops.Add(polygon); remainingPoints -= points.Count;
                        }
                        row["polygonsMm"] = loops;
                    }
                    surfaceRows.Add(row);
                }
                catch (Exception ex) { errors.Add(Error("surface " + s.Id.Value, ex)); }
            }
            result["energyModelId"] = model.Id.Value; result["tier"] = model.Tier.ToString();
            result["totalSpaces"] = spaces.Count; result["totalSurfaces"] = surfaces.Count;
            result["spaces"] = spaceRows; result["surfaces"] = surfaceRows; result["readErrors"] = errors;
            result["truncated"] = spaces.Count > limit || surfaces.Count > limit;
            result["geometryTruncated"] = geometryTruncated;
            result["sourceReferenceWarning"] = "Original element/link references reflect model-generation time and may be stale. No freshness claim is made.";
            return result;
        }

        static object SpatialDiagnostics(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp); Data(doc);
            int limit = Limit(args, "limit", 200, 2000), count = 0, issueCount = 0, errorCount = 0;
            JArray issues = new JArray(), errors = new JArray();
            foreach (SpatialElement element in new FilteredElementCollector(doc).OfClass(typeof(SpatialElement)).Cast<SpatialElement>())
            {
                if (!(element is Room) && !(element is Space)) continue;
                count++;
                try
                {
                    double area = element is Room ? ((Room)element).Area : ((Space)element).Area;
                    double volume = element is Room ? ((Room)element).Volume : ((Space)element).Volume;
                    JArray flags = new JArray();
                    if (element.Location == null) flags.Add("unplaced");
                    if (area <= 0) flags.Add("zeroAreaOrNotEnclosed");
                    if (volume <= 0) flags.Add("zeroVolumeOrVolumeCalculationDisabled");
                    if (flags.Count > 0)
                    {
                        issueCount++;
                        if (issues.Count < limit) issues.Add(new JObject(new JProperty("elementId", element.Id.Value), new JProperty("kind", element is Room ? "Room" : "Space"), new JProperty("name", element.Name), new JProperty("areaM2", area * Ft2ToM2), new JProperty("volumeM3", volume * Ft3ToM3), new JProperty("issues", flags)));
                    }
                }
                catch (Exception ex) { errorCount++; if (errors.Count < limit) errors.Add(Error("element " + element.Id.Value, ex)); }
            }
            return new JObject(new JProperty("checked", count), new JProperty("issueCount", issueCount), new JProperty("issues", issues), new JProperty("readErrors", errors),
                new JProperty("readErrorCount", errorCount), new JProperty("truncated", issueCount > issues.Count || errorCount > errors.Count), new JProperty("scope", "Host-document rooms/spaces; all phases. Does not check linked spatial elements, complete thermal enclosure or material correctness."));
        }

        static object CreateModel(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp);
            EnergyDataSettings data = Data(doc);
            EnergyAnalysisDetailModel current = EnergyAnalysisDetailModel.GetMainEnergyAnalysisDetailModel(doc);
            bool replace = Flag(args, "replaceExisting", false), dryRun = Flag(args, "dryRun", true);
            EnergyModelType type = (EnergyModelType)EnumValue(typeof(EnergyModelType), StringArg(args, "modelType", "AnalysisMode"), "modelType");
            EnergyAnalysisDetailModelTier tier = (EnergyAnalysisDetailModelTier)EnumValue(typeof(EnergyAnalysisDetailModelTier), StringArg(args, "tier", "Final"), "tier");
            if (tier == EnergyAnalysisDetailModelTier.NotComputed) throw new ArgumentException("tier must request a computed model.");
            if (current != null && !replace) throw new InvalidOperationException("Main energy model already exists. Use get_energy_model, or explicitly set replaceExisting=true to rebuild.");
            if (data.UseCurrentViewOnly && data.AnalysisType != AnalysisMode.RoomsOrSpaces)
            {
                if (!(doc.ActiveView is View3D) || !A.Has(args, "expectedViewId") || args["expectedViewId"].Type != JTokenType.Integer || args["expectedViewId"].Value<long>() != doc.ActiveView.Id.Value)
                    throw new InvalidOperationException("useCurrentViewOnly requires the intended active 3D view and its expectedViewId.");
            }
            using (EnergyAnalysisDetailModelOptions options = new EnergyAnalysisDetailModelOptions())
            {
                options.EnergyModelType = type; options.Tier = tier;
                options.IncludeShadingSurfaces = Flag(args, "includeShadingSurfaces", true);
                options.ExportMullions = Flag(args, "exportMullions", false);
                options.SimplifyCurtainSystems = Flag(args, "simplifyCurtainSystems", true);
                JObject result = new JObject(new JProperty("dryRun", dryRun), new JProperty("modelType", type.ToString()), new JProperty("tier", tier.ToString()),
                    new JProperty("replaceExistingId", current == null ? JValue.CreateNull() : new JValue(current.Id.Value)), new JProperty("sourceGeometryChanged", false),
                    new JProperty("includeShadingSurfaces", options.IncludeShadingSurfaces), new JProperty("exportMullions", options.ExportMullions), new JProperty("simplifyCurtainSystems", options.SimplifyCurtainSystems));
                if (dryRun) return result;
                EnergyAnalysisDetailModel created = Rx.Tx(doc, "MCP: Build energy model", delegate {
                    if (current != null) result["deletedElementIds"] = new JArray(doc.Delete(current.Id).Select(delegate(ElementId id) { return id.Value; }));
                    EnergyAnalysisDetailModel model = EnergyAnalysisDetailModel.Create(doc, options);
                    if (model == null) throw new InvalidOperationException("Revit returned no energy model.");
                    return model;
                });
                result["energyModelId"] = created.Id.Value;
                result["created"] = true;
                result["note"] = "Generated analytical model retained in document. Physical geometry was not edited. Review it before simulation.";
                return result;
            }
        }

        static object ExportGbxml(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp); Data(doc);
            if (EnergyAnalysisDetailModel.GetMainEnergyAnalysisDetailModel(doc) == null) throw new InvalidOperationException("Create and verify the main energy model before gbXML export.");
            string path = LocalPath(StringArg(args, "outputPath", null), "outputPath");
            if (!string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("outputPath must end with .xml.");
            if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("outputPath parent folder does not exist.");
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("outputPath already exists. Supply a new file name.");
            ExportEnergyModelType type = (ExportEnergyModelType)EnumValue(typeof(ExportEnergyModelType), StringArg(args, "modelType", "AnalysisMode"), "modelType");
            bool analyticalSystems = Flag(args, "exportAnalyticalSystems", true);
            JObject result = new JObject(new JProperty("dryRun", Flag(args, "dryRun", true)), new JProperty("outputPath", path), new JProperty("modelType", type.ToString()), new JProperty("exportAnalyticalSystems", analyticalSystems));
            if ((bool)result["dryRun"]) return result;
            string staging = Path.Combine(Path.GetDirectoryName(path), ".revit-mcp-gbxml-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            using (GBXMLExportOptions options = new GBXMLExportOptions())
            {
                options.ExportEnergyModelType = type; options.ExportAnalyticalSystems = analyticalSystems;
                // API consumes existing main model and requires a matching type; it never silently rebuilds here.
                try
                {
                    if (!doc.Export(staging, Path.GetFileName(path), options)) throw new IOException("Revit gbXML export returned false.");
                    string stagedFile = Path.Combine(staging, Path.GetFileName(path));
                    if (!File.Exists(stagedFile) || new FileInfo(stagedFile).Length == 0) throw new IOException("Revit did not produce a nonempty gbXML file.");
                    // Move fails if another process created the destination; no overwrite even with a path race.
                    File.Move(stagedFile, path);
                }
                catch (Exception ex) { throw new IOException("gbXML export failed; partial artifacts, if any, are retained at " + staging, ex); }
            }
            result["exported"] = true;
            result["bytes"] = new FileInfo(path).Length;
            // Keep even the empty staging folder as an audit location; no cleanup failure can obscure successful export.
            result["stagingFolder"] = staging;
            return result;
        }

        static object RequestSystems(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp); Data(doc);
            if (EnergyAnalysisDetailModel.GetMainEnergyAnalysisDetailModel(doc) == null) throw new InvalidOperationException("A valid main energy model is required before systems analysis.");
            string name = StringArg(args, "reportName", null);
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny("{}[]|;<>?`~".ToCharArray()) >= 0) throw new ArgumentException("reportName is empty or contains prohibited view-name characters.");
            if (new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Any(delegate(View view) { return string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase); }))
                throw new ArgumentException("A view with reportName already exists; choose a new report name.");
            string weather = ExistingFile(args, "weatherFile", ".epw"), workflow = ExistingFile(args, "workflowFile", ".osw");
            string folder = LocalPath(StringArg(args, "outputFolder", null), "outputFolder");
            if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) throw new ArgumentException("outputFolder must exist and be empty, dedicated to this analysis.");
            JObject result = new JObject(new JProperty("dryRun", Flag(args, "dryRun", true)), new JProperty("reportName", name),
                new JProperty("weatherFile", weather), new JProperty("workflowFile", workflow), new JProperty("outputFolder", folder),
                new JProperty("note", "Workflow may launch the installed analysis engine and execute its configured measures. Analysis result must be reviewed separately."));
            if ((bool)result["dryRun"]) return result;
            using (SystemsAnalysisOptions options = new SystemsAnalysisOptions())
            {
                options.WeatherFile = weather; options.WorkflowFile = workflow; options.OutputFolder = folder;
                ViewSystemsAnalysisReport report = Rx.Tx(doc, "MCP: Create systems analysis report", delegate {
                    ViewSystemsAnalysisReport created = ViewSystemsAnalysisReport.Create(doc, name);
                    if (created == null) throw new InvalidOperationException("Revit returned no systems analysis report.");
                    return created;
                });
                result["reportId"] = report.Id.Value;
                try { Rx.Tx(doc, "MCP: Request systems analysis", delegate { report.RequestSystemsAnalysis(options); return true; }); }
                catch (Exception ex)
                {
                    // Report creation has committed; retain its ID so callers can inspect/recover it, never blindly resubmit.
                    throw new InvalidOperationException("Systems-analysis request did not return successfully. Retained reportId=" + report.Id.Value + ". Inspect this report and output folder before retrying; background launch state may be uncertain.", ex);
                }
                result["requested"] = true; result["nextTool"] = "get_systems_analysis_status";
                return result;
            }
        }

        static ViewSystemsAnalysisReport Report(Document doc, JObject args, bool required)
        {
            ViewSystemsAnalysisReport report;
            if (A.Has(args, "reportId"))
            {
                if (args["reportId"].Type != JTokenType.Integer) throw new ArgumentException("reportId must be an integer.");
                report = doc.GetElement(new ElementId(args["reportId"].Value<long>())) as ViewSystemsAnalysisReport;
                if (report == null) throw new ArgumentException("reportId is not a systems analysis report in this document.");
            }
            else
            {
                if (required) throw new ArgumentException("reportId is required.");
                ElementId latest = ViewSystemsAnalysisReport.GetLatestSystemsAnalysisReport(doc);
                report = latest == null || latest == ElementId.InvalidElementId ? null : doc.GetElement(latest) as ViewSystemsAnalysisReport;
            }
            return report;
        }

        static object SystemsStatus(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp); Data(doc);
            ViewSystemsAnalysisReport report = Report(doc, args, false);
            JObject result = new JObject(new JProperty("exists", report != null));
            if (report == null) return result;
            result["reportId"] = report.Id.Value; result["name"] = report.Name;
            result["completed"] = report.IsAnalysisCompleted();
            result["analysisDateAndTime"] = JToken.FromObject(report.AnalysisDateAndTime);
            result["outputFolder"] = report.SystemsAnalysisOutputFolder; result["weatherFile"] = report.WeatherFile; result["workflowFile"] = report.SystemsAnalysisWorkflowFile;
            result["resultInterpretation"] = "completed only indicates the background calculation ended. Review report/logs for success, warnings and errors.";
            if (Flag(args, "includeContent", false))
            {
                int max = Limit(args, "maxContentChars", 20000, 100000);
                string content = report.GetReportContent() ?? "";
                result["content"] = content.Length > max ? content.Substring(0, max) : content;
                result["contentTruncated"] = content.Length > max;
                result["contentIsUntrusted"] = true;
            }
            return result;
        }

        static object CancelSystems(UIApplication uiapp, JObject args)
        {
            Document doc = Rx.Doc(uiapp); Data(doc);
            ViewSystemsAnalysisReport report = Report(doc, args, true);
            bool completed = report.IsAnalysisCompleted();
            JObject result = new JObject(new JProperty("dryRun", Flag(args, "dryRun", true)), new JProperty("reportId", report.Id.Value), new JProperty("alreadyCompleted", completed));
            if ((bool)result["dryRun"] || completed) { result["cancellationRequested"] = false; return result; }
            ViewSystemsAnalysisReport.CancelSystemsAnalysis(doc, report.Id);
            result["cancellationRequested"] = true;
            result["note"] = "Cancellation requested; poll report status. Existing report and output files are retained.";
            return result;
        }

        static JObject Error(string field, Exception ex)
        {
            Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return new JObject(new JProperty("item", field), new JProperty("error", cause.GetType().Name + ": " + cause.Message));
        }
        static string Text(JToken token, string name)
        {
            if (token == null || token.Type != JTokenType.String) throw new ArgumentException(name + " must be a string.");
            return token.Value<string>();
        }
        static string StringArg(JObject args, string name, string fallback) { return A.Has(args, name) ? Text(args[name], name) : fallback; }
        static double Numeric(JToken token, string name)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) throw new ArgumentException(name + " must be numeric.");
            double n = token.Value<double>();
            if (double.IsNaN(n) || double.IsInfinity(n)) throw new ArgumentException(name + " must be finite.");
            return n;
        }
        static bool Flag(JObject args, string name, bool fallback)
        {
            if (!A.Has(args, name)) return fallback;
            if (args[name].Type != JTokenType.Boolean) throw new ArgumentException(name + " must be boolean.");
            return args[name].Value<bool>();
        }
        static int Limit(JObject args, string name, int fallback, int max)
        {
            if (!A.Has(args, name)) return fallback;
            if (args[name].Type != JTokenType.Integer) throw new ArgumentException(name + " must be integer.");
            long n = args[name].Value<long>();
            if (n < 1 || n > max) throw new ArgumentOutOfRangeException(name, "Must be between 1 and " + max + ".");
            return (int)n;
        }
        static object EnumValue(Type type, string value, string name)
        {
            if (value == null || !Enum.GetNames(type).Contains(value)) throw new ArgumentException(name + " must be one of: " + string.Join(", ", Enum.GetNames(type)));
            return Enum.Parse(type, value, false);
        }
        static string LocalPath(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 3 || !char.IsLetter(value[0]) || value[1] != ':' || (value[2] != '\\' && value[2] != '/') || value.IndexOf(':', 2) >= 0)
                throw new ArgumentException(name + " must be an absolute local path without alternate data streams.");
            return Path.GetFullPath(value);
        }
        static string ExistingFile(JObject args, string name, string extension)
        {
            string path = LocalPath(StringArg(args, name, null), name);
            if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new ArgumentException(name + " must be an existing " + extension + " file.");
            return path;
        }
    }
}
