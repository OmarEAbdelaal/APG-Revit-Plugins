using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core.Dm
{
    /// <summary>What the audit should check.</summary>
    public sealed class DmAuditOptions
    {
        /// <summary>Permit stage the Appendix B "Required" column is read for.</summary>
        public DmPermitStage Stage { get; set; } = DmPermitStage.Final;

        /// <summary>Also report attributes DM marks "Conditional" (unit attributes and similar).</summary>
        public bool IncludeConditional { get; set; }

        /// <summary>Check family and type names against the DM object naming convention.</summary>
        public bool CheckObjectNaming { get; set; } = true;

        /// <summary>
        /// Check DM's recommended modelling practices (wall and column constraints, level
        /// association, space coverage and height, finishes, link clashes …). They read the
        /// model geometry, so they are the slowest phase of the audit.
        /// </summary>
        public bool CheckModellingPractices { get; set; } = true;

        /// <summary>Maximum element ids kept per finding (protects the dashboard on huge models).</summary>
        public int MaxElementsPerFinding { get; set; } = 20000;
    }

    /// <summary>
    /// Reads an open Revit model and reports everything Dubai Municipality would reject at
    /// BIM e-submission: missing project/site/building attributes, level naming, room and unit
    /// data, the Appendix B element attributes and DM's own IDS rules, object naming,
    /// geo-referencing and export readiness.
    ///
    /// Read-only: the audit never modifies the model. Every finding carries the elements it
    /// applies to, the type of modification needed and a ready-made Revit MCP prompt.
    /// </summary>
    public static partial class DmAuditService
    {
        private static readonly Regex FileNamePattern =
            new Regex(@"^PN\d{6}_BI\d{6}_PA\d{7,8}_(AR|ST)_\d{3}$", RegexOptions.CultureInvariant);

        private static readonly Regex LevelNamePattern =
            new Regex(@"^[A-Z0-9]+_[A-Z0-9][A-Z0-9 _.\-]*$", RegexOptions.CultureInvariant);

        private static readonly Regex RoomNumberPattern =
            new Regex(@"^[A-Za-z0-9]{1,4}-\d{3}$", RegexOptions.CultureInvariant);

        public static DmAuditResult Run(Document doc, string revitVersion, DmAuditOptions options)
        {
            var result = new DmAuditResult
            {
                ModelTitle = doc.Title,
                ModelPath = doc.PathName ?? "",
                ProjectName = doc.ProjectInformation?.Name ?? "",
                ProjectNumber = doc.ProjectInformation?.Number ?? "",
                Stage = options.Stage,
                IncludeConditional = options.IncludeConditional,
                RevitVersion = revitVersion,
                KnowledgeBaseSource = DmKnowledgeBase.Source
            };

            // Write the DM shared parameter file up front: the fix scripts in the prompts point
            // at it, so the definitions are on disk before anyone runs one.
            try
            {
                DmSharedParameters.WriteFile();
            }
            catch
            {
                // a read-only Documents folder must not stop the audit
            }

            var parameters = new DmParameters(doc);

            CheckProjectInformation(doc, result, parameters, options);
            CheckLevels(doc, result, parameters, options);
            CheckRoomsAndSpaces(doc, result, parameters, options);
            CheckElementAttributes(doc, result, parameters, options);
            if (options.CheckObjectNaming)
                CheckObjectNaming(doc, result);
            CheckGeoReferencing(doc, result, parameters);
            if (options.CheckModellingPractices)
                CheckModellingPractices(doc, result, parameters, options);
            CheckExportReadiness(doc, result);

            return result;
        }

        // ── 1. Project / Site / Building ────────────────────────────────────────

        private static void CheckProjectInformation(Document doc, DmAuditResult result,
                                                    DmParameters parameters, DmAuditOptions options)
        {
            ProjectInfo? info = doc.ProjectInformation;
            if (info == null)
                return;

            int before = result.Findings.Count;

            // Revit's own project information fields, which DM maps onto IfcProject/IfcBuilding.
            var builtIn = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Project Name", info.Name ?? ""),
                new KeyValuePair<string, string>("Project Number", info.Number ?? ""),
                new KeyValuePair<string, string>("Client Name", info.ClientName ?? ""),
                new KeyValuePair<string, string>("Organization Name", info.OrganizationName ?? ""),
                new KeyValuePair<string, string>("Building Name", info.BuildingName ?? ""),
                new KeyValuePair<string, string>("Project Address", info.Address ?? ""),
                new KeyValuePair<string, string>("Author", info.Author ?? ""),
                new KeyValuePair<string, string>("Project Status", info.Status ?? "")
            };

            var emptyFields = builtIn
                .Where(f => string.IsNullOrWhiteSpace(f.Value) ||
                            f.Value.Equals("Project Name", StringComparison.OrdinalIgnoreCase) ||
                            f.Value.Equals("Project Number", StringComparison.OrdinalIgnoreCase) ||
                            f.Value.Equals("Owner", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Key)
                .ToList();

            if (emptyFields.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ProjectInformation,
                    Severity = DmSeverity.Critical,
                    Scope = "Project Information",
                    Title = emptyFields.Count + " Revit project information field(s) are empty",
                    Detail = "DM reads these fields into IfcProject and IfcBuilding: " +
                             string.Join(", ", emptyFields) + ". Empty values are exported as empty " +
                             "IFC attributes and rejected by the QA/QC checker.",
                    Reference = "Dubai BIM Standard, Appendix B — Project / Building",
                    FixKind = DmFixKind.SetParameter,
                    FixAction = "Fill " + string.Join(", ", emptyFields) +
                                " in Manage ▸ Project Information.",
                    CheckedCount = builtIn.Count,
                    AffectedCount = emptyFields.Count
                }, result.ModelTitle);
            }

            // DM attributes on IfcProject / IfcSite / IfcBuilding, from Appendix B and the IDS file.
            var required = new List<DmAttribute>();
            foreach (string table in new[] { "Project", "Building", "Topography_Site" })
                required.AddRange(DmKnowledgeBase.RequiredAttributes(table, options.Stage, options.IncludeConditional));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var attributes = new List<DmAttribute>();
            foreach (DmAttribute attribute in required)
            {
                if (IsRevitBuiltInProjectField(attribute.Name))
                    continue;
                if (seen.Add(attribute.Name))
                    attributes.Add(attribute);
            }
            foreach (string entity in new[] { "IFCPROJECT", "IFCSITE", "IFCBUILDING" })
            {
                foreach (DmIdsRequirement requirement in DmKnowledgeBase.IdsPropertiesFor(entity))
                {
                    if (seen.Add(requirement.BaseName))
                        attributes.Add(new DmAttribute
                        {
                            Name = requirement.BaseName,
                            PropertySet = requirement.PropertySet,
                            UserInput = true,
                            Description = "Required by DM's IDS rule set for " + entity + "."
                        });
                }
            }

            var notBound = new List<string>();
            var empty = new List<DmAttribute>();
            foreach (DmAttribute attribute in attributes)
            {
                DmParameterState state = parameters.State(info, attribute.Name);
                if (state == DmParameterState.NotBound)
                    notBound.Add(attribute.Name);
                else if (state == DmParameterState.Empty)
                    empty.Add(attribute);
            }

            if (notBound.Count > 0)
            {
                var binding = new DmFinding
                {
                    Group = DmCheckGroup.ProjectInformation,
                    Severity = DmSeverity.Critical,
                    Scope = "Project Information",
                    Title = notBound.Count + " DM attribute(s) do not exist on Project Information",
                    Detail = "The DM shared parameters are not bound to the Project Information " +
                             "category, so these attributes cannot be exported at all: " +
                             string.Join(", ", notBound.Take(30)) +
                             (notBound.Count > 30 ? " …" : "") + ".",
                    Reference = "Dubai BIM e-Submission shared parameter file, Appendix B",
                    FixKind = DmFixKind.BindParameter,
                    FixAction = "Bind the listed DM attributes to the Project Information category. " +
                                "The plugin ships the definitions: click \"Bind DM parameters\" in the " +
                                "dashboard, or run the script in the prompt.",
                    CheckedCount = attributes.Count,
                    AffectedCount = notBound.Count,
                    Table = "Project"
                };
                binding.ParametersToBind.AddRange(notBound);
                binding.Categories.Add(BuiltInCategory.OST_ProjectInformation.ToString());
                Add(result, binding, result.ModelTitle);
            }

            foreach (DmAttribute attribute in empty)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ProjectInformation,
                    Severity = DmSeverity.Error,
                    Scope = "Project Information",
                    Title = "\"" + attribute.Name + "\" is empty on Project Information",
                    Detail = (attribute.Description.Length > 0
                                 ? attribute.Description
                                 : "Required attribute of the project/site/building entity.") +
                             (attribute.PropertySet.Length > 0
                                 ? "  Property set: " + attribute.PropertySet + "."
                                 : ""),
                    Reference = "Dubai BIM Standard, Appendix B",
                    FixKind = DmFixKind.SetParameter,
                    ParameterName = attribute.Name,
                    SampleValue = attribute.Sample,
                    FixAction = "Enter a value for \"" + attribute.Name + "\" in Manage ▸ Project Information" +
                                (attribute.Sample.Length > 0 ? " (DM sample: " + attribute.Sample + ")" : "") + ".",
                    CheckedCount = 1,
                    AffectedCount = 1,
                    Table = "Project"
                }, result.ModelTitle, new List<Element> { info }, options);
            }

            // BIMStandardVersion must name the standard the model was prepared against.
            string standardVersion = parameters.Value(info, "BIMStandardVersion");
            if (standardVersion.Length > 0 && !standardVersion.StartsWith(DmKnowledgeBase.StandardVersion, StringComparison.Ordinal))
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ProjectInformation,
                    Severity = DmSeverity.Warning,
                    Scope = "Project Information",
                    Title = "BIMStandardVersion is \"" + standardVersion + "\", the current DM standard is " +
                            DmKnowledgeBase.StandardVersion,
                    Detail = "The model declares an older version of the Dubai BIM Standard than the one " +
                             "this audit checks against. Confirm which version the permit is submitted under.",
                    Reference = "Dubai BIM Standard " + DmKnowledgeBase.StandardVersion + " (changelog 2026-06-08)",
                    FixKind = DmFixKind.SetParameter,
                    ParameterName = "BIMStandardVersion",
                    SampleValue = DmKnowledgeBase.StandardVersion,
                    FixAction = "Set BIMStandardVersion to " + DmKnowledgeBase.StandardVersion +
                                " once the model follows that revision of the standard.",
                    CheckedCount = 1,
                    AffectedCount = 1
                }, result.ModelTitle);
            }

            Summarize(result, DmCheckGroup.ProjectInformation, "Project, site and building attributes",
                      builtIn.Count + attributes.Count, before);
        }

        private static bool IsRevitBuiltInProjectField(string name)
        {
            switch (name)
            {
                case "Description":
                case "LongName":
                case "Name":
                case "Address":
                case "BuildingAddress":
                    return true;
                default:
                    return false;
            }
        }

        // ── 2. Levels ───────────────────────────────────────────────────────────

        private static void CheckLevels(Document doc, DmAuditResult result,
                                        DmParameters parameters, DmAuditOptions options)
        {
            int before = result.Findings.Count;

            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .WhereElementIsNotElementType()
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            if (levels.Count == 0)
                return;

            var planLevelIds = new HashSet<long>();
            foreach (ViewPlan view in new FilteredElementCollector(doc)
                         .OfClass(typeof(ViewPlan))
                         .Cast<ViewPlan>())
            {
                if (view.IsTemplate || view.GenLevel == null)
                    continue;
                planLevelIds.Add(view.GenLevel.Id.Value);
            }

            var badNames = new List<Element>();
            var noPlanView = new List<Element>();
            var dummyExported = new List<Element>();
            var buildingStoreys = new List<Level>();
            var byName = new Dictionary<string, List<Element>>(StringComparer.OrdinalIgnoreCase);

            foreach (Level level in levels)
            {
                bool isBuildingStorey = IsBuildingStorey(level);
                if (isBuildingStorey)
                    buildingStoreys.Add(level);

                if (!byName.TryGetValue(level.Name, out List<Element>? list))
                {
                    list = new List<Element>();
                    byName[level.Name] = list;
                }
                list.Add(level);

                if (!isBuildingStorey)
                    continue;

                if (!LevelNamePattern.IsMatch(level.Name))
                    badNames.Add(level);
                if (!planLevelIds.Contains(level.Id.Value))
                    noPlanView.Add(level);
                if (LooksLikeReferenceLevel(level.Name))
                    dummyExported.Add(level);
            }

            if (badNames.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.Levels,
                    Severity = DmSeverity.Critical,
                    Scope = "Levels",
                    Title = badNames.Count + " level name(s) do not follow the DM convention",
                    Detail = "Level names must be UPPERCASE \"Abbreviation_Identification\", e.g. F1_FLOOR1, " +
                             "B1_BASEMENT1, GA_GATE LEVEL, RF_ROOF. Architectural and structural models of " +
                             "the same building must use identical level names — a frequent rejection reason.",
                    Reference = "Dubai BIM Standard Part 2 §6-7 — level naming convention",
                    FixKind = DmFixKind.Rename,
                    FixAction = "Rename the levels to ABBREVIATION_IDENTIFICATION in uppercase and keep the " +
                                "same names in the structural model.",
                    CheckedCount = buildingStoreys.Count,
                    AffectedCount = badNames.Count,
                    ReferenceData = DmReferenceData.LevelNaming()
                }, result.ModelTitle, badNames, options);
            }

            var duplicates = byName.Where(p => p.Value.Count > 1).ToList();
            if (duplicates.Count > 0)
            {
                var elements = duplicates.SelectMany(p => p.Value).ToList();
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.Levels,
                    Severity = DmSeverity.Critical,
                    Scope = "Levels",
                    Title = duplicates.Count + " level name(s) are used more than once",
                    Detail = "Duplicated storey names (" +
                             string.Join(", ", duplicates.Take(10).Select(p => p.Key)) +
                             ") make the IfcBuildingStorey mapping ambiguous and break the building card.",
                    Reference = "Dubai BIM Standard Part 2 §6-7",
                    FixKind = DmFixKind.Rename,
                    FixAction = "Give every level a unique name.",
                    CheckedCount = levels.Count,
                    AffectedCount = elements.Count
                }, result.ModelTitle, elements, options);
            }

            bool hasGateLevel = levels.Any(l => l.Name.StartsWith("GA", StringComparison.OrdinalIgnoreCase) ||
                                                l.Name.IndexOf("GATE", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hasGateLevel)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.Levels,
                    Severity = DmSeverity.Critical,
                    Scope = "Levels",
                    Title = "No gate level (GA_GATE LEVEL) exists in the model",
                    Detail = "Every DM model must carry the gate level: it is the vertical reference of the " +
                             "project, aligned to the Revit internal origin, and its elevation is the DMD " +
                             "value exported as the IfcSite Z coordinate.",
                    Reference = "Dubai BIM Standard Part 2 §7 — mandatory gate level",
                    FixKind = DmFixKind.ModelChange,
                    FixAction = "Add the level GA_GATE LEVEL at the official DMD elevation and align the " +
                                "internal origin to it.",
                    CheckedCount = levels.Count,
                    AffectedCount = 1
                }, result.ModelTitle);
            }

            if (noPlanView.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.Levels,
                    Severity = DmSeverity.Warning,
                    Scope = "Levels",
                    Title = noPlanView.Count + " building storey level(s) have no floor plan view",
                    Detail = "DM expects every storey of the model to match a design level in the 2D " +
                             "submission, which is only possible when the level has its own plan view.",
                    Reference = "DM offline self-assessment checklist — naming conventions",
                    FixKind = DmFixKind.ModelChange,
                    FixAction = "Create a floor plan view for each listed level, or clear \"Building Story\" " +
                                "when the level is only a reference.",
                    CheckedCount = buildingStoreys.Count,
                    AffectedCount = noPlanView.Count
                }, result.ModelTitle, noPlanView, options);
            }

            if (dummyExported.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.Levels,
                    Severity = DmSeverity.Error,
                    Scope = "Levels",
                    Title = dummyExported.Count + " reference/dummy level(s) are still flagged as building storeys",
                    Detail = "Levels whose name reads as a reference (TOS, SSL, REF, DUMMY, T.O.) export as " +
                             "IfcBuildingStorey and appear as extra floors in DM's building card.",
                    Reference = "Dubai BIM Standard Part 2 §7 — dummy levels excluded from export",
                    FixKind = DmFixKind.SetParameter,
                    ParameterName = "Building Story",
                    FixAction = "Clear the \"Building Story\" parameter on these levels before exporting to IFC.",
                    CheckedCount = buildingStoreys.Count,
                    AffectedCount = dummyExported.Count
                }, result.ModelTitle, dummyExported, options);
            }

            // Appendix B storey attributes (areas per level).
            List<DmAttribute> storeyAttributes =
                DmKnowledgeBase.RequiredAttributes("Storey", options.Stage, options.IncludeConditional)
                    .Where(a => a.UserInput)
                    .ToList();

            foreach (DmAttribute attribute in storeyAttributes)
            {
                var missing = new List<Element>();
                bool anyBound = false;
                foreach (Level level in buildingStoreys)
                {
                    DmParameterState state = parameters.State(level, attribute.Name);
                    if (state == DmParameterState.Filled)
                    {
                        anyBound = true;
                        continue;
                    }
                    if (state == DmParameterState.Empty)
                        anyBound = true;
                    missing.Add(level);
                }
                if (missing.Count == 0)
                    continue;

                var storeyFinding = new DmFinding
                {
                    Group = DmCheckGroup.Levels,
                    Severity = anyBound ? DmSeverity.Error : DmSeverity.Critical,
                    Scope = "Levels",
                    Title = "\"" + attribute.Name + "\" missing on " + missing.Count + " of " +
                            buildingStoreys.Count + " building storeys",
                    Detail = attribute.Description.Length > 0
                        ? attribute.Description
                        : "Required storey attribute of the DM building permit property set.",
                    Reference = "Dubai BIM Standard, Appendix B — Storey (IfcBuildingStorey)",
                    FixKind = anyBound ? DmFixKind.SetParameter : DmFixKind.BindParameter,
                    ParameterName = attribute.Name,
                    SampleValue = attribute.Sample,
                    FixAction = anyBound
                        ? "Fill \"" + attribute.Name + "\" on each building storey with the area from the " +
                          "submitted area statement (m²)."
                        : "Bind the DM shared parameter \"" + attribute.Name + "\" to the Levels category, " +
                          "then fill it per storey. The plugin ships the definition — use \"Bind DM " +
                          "parameters\" in the dashboard or the script in the prompt.",
                    CheckedCount = buildingStoreys.Count,
                    AffectedCount = missing.Count,
                    Table = "Storey"
                };
                storeyFinding.Categories.Add(BuiltInCategory.OST_Levels.ToString());
                if (!anyBound)
                    storeyFinding.ParametersToBind.Add(attribute.Name);
                Add(result, storeyFinding, result.ModelTitle, missing, options);
            }

            Summarize(result, DmCheckGroup.Levels, "Level naming, gate level and storey attributes",
                      levels.Count, before);
        }

        private static bool IsBuildingStorey(Level level)
        {
            Parameter? parameter = level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
            return parameter == null || !parameter.HasValue || parameter.AsInteger() != 0;
        }

        private static bool LooksLikeReferenceLevel(string name)
        {
            string upper = name.ToUpperInvariant();
            return upper.Contains("DUMMY") || upper.Contains("REFERENCE") || upper.Contains("REF LEVEL") ||
                   upper.Contains("T.O.") || upper.Contains("TOS ") || upper.StartsWith("TOS", StringComparison.Ordinal) ||
                   upper.Contains("SSL") || upper.Contains("WORKING") || upper.Contains("TEMP");
        }

        // ── 6. Geo-referencing and units ────────────────────────────────────────

        private static void CheckGeoReferencing(Document doc, DmAuditResult result, DmParameters parameters)
        {
            int before = result.Findings.Count;

            Element? surveyPoint = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_SharedBasePoint)
                .WhereElementIsNotElementType()
                .FirstOrDefault();

            if (surveyPoint != null)
            {
                double northSouth = FeetToMeters(ParameterValue(surveyPoint, BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM));
                double eastWest = FeetToMeters(ParameterValue(surveyPoint, BuiltInParameter.BASEPOINT_EASTWEST_PARAM));

                if (Math.Abs(northSouth) < 0.01 && Math.Abs(eastWest) < 0.01)
                {
                    Add(result, new DmFinding
                    {
                        Group = DmCheckGroup.GeoReferencing,
                        Severity = DmSeverity.Critical,
                        Scope = "Survey point",
                        Title = "The survey point is still at 0,0 — the model is not geo-referenced",
                        Detail = "DM requires every IFC to carry easting, northing and DMD elevation on " +
                                 "IfcSite, in EPSG:3997 (WGS 84 / Dubai Local TM). An unmoved survey point " +
                                 "means the parcel coordinates were never acquired.",
                        Reference = "Dubai BIM Standard §4.1.2 / Technical Guides Part 2.6 — georeferencing",
                        FixKind = DmFixKind.ProjectSetup,
                        FixAction = "Download the parcel package from Build in Dubai, link PARCELS.dxf in " +
                                    "metres, then Manage ▸ Coordinates ▸ Acquire Coordinates, and set the gate " +
                                    "level elevation with \"Specify Coordinates at Point\".",
                        CheckedCount = 1,
                        AffectedCount = 1
                    }, result.ModelTitle, new List<Element> { surveyPoint }, new DmAuditOptions());
                }
            }

            SiteLocation? site = doc.ActiveProjectLocation?.GetSiteLocation();
            if (site != null)
            {
                double latitude = site.Latitude * 180.0 / Math.PI;
                double longitude = site.Longitude * 180.0 / Math.PI;
                bool inDubai = latitude > 24.0 && latitude < 26.5 && longitude > 54.0 && longitude < 56.5;
                if (!inDubai)
                {
                    Add(result, new DmFinding
                    {
                        Group = DmCheckGroup.GeoReferencing,
                        Severity = DmSeverity.Critical,
                        Scope = "Project location",
                        Title = "Project location is outside Dubai (" +
                                latitude.ToString("F4", CultureInfo.InvariantCulture) + ", " +
                                longitude.ToString("F4", CultureInfo.InvariantCulture) + ")",
                        Detail = "The site location must place the project inside the Dubai region so the " +
                                 "footprint falls within the parcel boundary in the Build in Dubai viewer.",
                        Reference = "Dubai BIM Standard §7.7 — EPSG:3997 WGS 84 / Dubai Local TM",
                        FixKind = DmFixKind.ProjectSetup,
                        FixAction = "Manage ▸ Location: set the project location to the parcel, then acquire " +
                                    "the coordinates from the parcel DXF again.",
                        CheckedCount = 1,
                        AffectedCount = 1
                    }, result.ModelTitle);
                }
            }

            // Gate level elevation against the GateLevel attribute.
            ProjectInfo? info = doc.ProjectInformation;
            Level? gateLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name.StartsWith("GA", StringComparison.OrdinalIgnoreCase) ||
                                     l.Name.IndexOf("GATE", StringComparison.OrdinalIgnoreCase) >= 0);
            if (info != null && gateLevel != null)
            {
                Parameter? gateParameter = DmParameters.Lookup(info, "GateLevel");
                if (gateParameter != null && DmParameters.Classify(gateParameter) == DmParameterState.Filled &&
                    gateParameter.StorageType == StorageType.Double)
                {
                    double declared = gateParameter.AsDouble();
                    double actual = FeetToMeters(gateLevel.Elevation);
                    // The parameter may be a plain number (metres) or a length in internal units.
                    double declaredMeters = Math.Abs(declared) > 1000 ? FeetToMeters(declared) : declared;
                    if (Math.Abs(declaredMeters - actual) > 0.05 && Math.Abs(FeetToMeters(declared) - actual) > 0.05)
                    {
                        Add(result, new DmFinding
                        {
                            Group = DmCheckGroup.GeoReferencing,
                            Severity = DmSeverity.Error,
                            Scope = "Gate level",
                            Title = "GateLevel (" + declaredMeters.ToString("F3", CultureInfo.InvariantCulture) +
                                    " m) does not match the elevation of \"" + gateLevel.Name + "\" (" +
                                    actual.ToString("F3", CultureInfo.InvariantCulture) + " m)",
                            Detail = "The exported IfcSite Z value must equal the gate level elevation, which " +
                                     "in turn must be the official DMD elevation of the parcel.",
                            Reference = "Dubai BIM Standard §7.7 — gate level equals IfcSite elevation",
                            FixKind = DmFixKind.SetParameter,
                            ParameterName = "GateLevel",
                            FixAction = "Align the GateLevel attribute and the level elevation to the DMD value " +
                                        "of the parcel.",
                            CheckedCount = 1,
                            AffectedCount = 1
                        }, result.ModelTitle, new List<Element> { gateLevel }, new DmAuditOptions());
                    }
                }
            }

            // Length unit: DM requires metres at export time.
            try
            {
                FormatOptions format = doc.GetUnits().GetFormatOptions(SpecTypeId.Length);
                if (format.GetUnitTypeId() != UnitTypeId.Meters &&
                    format.GetUnitTypeId() != UnitTypeId.Millimeters &&
                    format.GetUnitTypeId() != UnitTypeId.Centimeters)
                {
                    Add(result, new DmFinding
                    {
                        Group = DmCheckGroup.GeoReferencing,
                        Severity = DmSeverity.Warning,
                        Scope = "Project units",
                        Title = "Project length units are not metric",
                        Detail = "IFC files must be delivered in metres. Imperial project units are a common " +
                                 "cause of wrong areas in DM's building card.",
                        Reference = "Dubai BIM Standard Part 2 §6 — model units in metres",
                        FixKind = DmFixKind.ProjectSetup,
                        FixAction = "Manage ▸ Project Units: set length to metres (or millimetres) before export.",
                        CheckedCount = 1,
                        AffectedCount = 1
                    }, result.ModelTitle);
                }
            }
            catch
            {
                // units API differences must never break the audit
            }

            Summarize(result, DmCheckGroup.GeoReferencing, "Survey point, site location, gate level, units", 4, before);
        }

        private static double ParameterValue(Element element, BuiltInParameter builtIn)
        {
            Parameter? parameter = element.get_Parameter(builtIn);
            return parameter != null && parameter.HasValue ? parameter.AsDouble() : 0.0;
        }

        private static double FeetToMeters(double feet)
        {
            return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
        }

        // ── 7. Export readiness ─────────────────────────────────────────────────

        private static void CheckExportReadiness(Document doc, DmAuditResult result)
        {
            int before = result.Findings.Count;
            var options = new DmAuditOptions();

            // File naming convention.
            string path = doc.PathName ?? "";
            if (path.Length > 0)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!FileNamePattern.IsMatch(name))
                {
                    Add(result, new DmFinding
                    {
                        Group = DmCheckGroup.ExportReadiness,
                        Severity = DmSeverity.Error,
                        Scope = "File naming",
                        Title = "Model file name \"" + name + "\" does not follow the DM convention",
                        Detail = "Submission files are named PN{6 digits}_BI{6 digits}_PA{7-8 digits}_" +
                                 "{AR|ST}_{3 digits}, e.g. PN123456_BI123456_PA1234567_AR_001. The parcel " +
                                 "id in the name must equal the ParcelId attribute inside the file.",
                        Reference = "Dubai BIM Standard Part 2 §6 — file naming convention",
                        FixKind = DmFixKind.Rename,
                        FixAction = "Name the exported IFC (and ideally the Revit file) per the convention and " +
                                    "keep the PA field identical to the ParcelId attribute.",
                        CheckedCount = 1,
                        AffectedCount = 1,
                        ReferenceData = DmReferenceData.FileNaming()
                    }, result.ModelTitle);
                }
                else
                {
                    string parcelFromName = name.Split('_')[2].Substring(2);
                    string parcelAttribute = new DmParameters(doc).Value(doc.ProjectInformation, "ParcelId")
                        .Replace(",", "").Replace(" ", "").Split('.')[0];
                    if (parcelAttribute.Length > 0 && parcelAttribute != parcelFromName)
                    {
                        Add(result, new DmFinding
                        {
                            Group = DmCheckGroup.ExportReadiness,
                            Severity = DmSeverity.Critical,
                            Scope = "File naming",
                            Title = "ParcelId (" + parcelAttribute + ") differs from the PA field of the file name (" +
                                    parcelFromName + ")",
                            Detail = "DM cross-checks the parcel id in the file name against the ParcelId " +
                                     "attribute in the file; a mismatch is an automatic rejection.",
                            Reference = "Dubai BIM Standard Part 2 §6 — parcel id cross-check",
                            FixKind = DmFixKind.SetParameter,
                            ParameterName = "ParcelId",
                            FixAction = "Correct either the ParcelId attribute or the file name so both carry " +
                                        "the parcel id of the affection plan.",
                            CheckedCount = 1,
                            AffectedCount = 1
                        }, result.ModelTitle);
                    }
                }
            }

            // CAD imports and links.
            List<Element> imports = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .WhereElementIsNotElementType()
                .ToList();
            var hardImports = imports.Where(i => i is ImportInstance instance && !instance.IsLinked).ToList();
            var cadLinks = imports.Where(i => i is ImportInstance instance && instance.IsLinked).ToList();

            if (hardImports.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ExportReadiness,
                    Severity = DmSeverity.Error,
                    Scope = "CAD imports",
                    Title = hardImports.Count + " imported CAD file(s) are inside the model",
                    Detail = "Imported (not linked) DWG/DXF geometry exports as IfcBuildingElementProxy or " +
                             "annotation and pollutes the submission model.",
                    Reference = "DM offline self-assessment — geometry and visualisation",
                    FixKind = DmFixKind.ModelChange,
                    FixAction = "Delete the imported CAD instances, or convert them to native elements before export.",
                    CheckedCount = imports.Count,
                    AffectedCount = hardImports.Count
                }, result.ModelTitle, hardImports, options);
            }

            if (cadLinks.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ExportReadiness,
                    Severity = DmSeverity.Warning,
                    Scope = "CAD links",
                    Title = cadLinks.Count + " CAD link(s) are loaded",
                    Detail = "CAD links must not be exported. They are useful for coordinate acquisition but " +
                             "have to be unloaded or hidden in the exported view.",
                    Reference = "Technical Guides Part 5.1.4 — additional content not exported",
                    FixKind = DmFixKind.Review,
                    FixAction = "Unload the CAD links before running the IFC export.",
                    CheckedCount = imports.Count,
                    AffectedCount = cadLinks.Count
                }, result.ModelTitle, cadLinks, options);
            }

            List<Element> revitLinks = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .WhereElementIsNotElementType()
                .ToList();
            if (revitLinks.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ExportReadiness,
                    Severity = DmSeverity.Warning,
                    Scope = "Revit links",
                    Title = revitLinks.Count + " Revit link(s) are loaded in this model",
                    Detail = "Each discipline and each building exports its own IFC file. No element may " +
                             "appear in two files, so linked models must be excluded from the export.",
                    Reference = "Dubai BIM Standard Part 2 §7 — model segregation, single source of elements",
                    FixKind = DmFixKind.Review,
                    FixAction = "In the IFC export setup keep \"Export linked files\" off (the DM setup already " +
                                "does), and confirm no linked element is duplicated in this model.",
                    CheckedCount = revitLinks.Count,
                    AffectedCount = revitLinks.Count
                }, result.ModelTitle, revitLinks, options);
            }

            // In-place families export as proxies rather than their real IFC class.
            var inPlace = new List<Element>();
            foreach (FamilyInstance instance in new FilteredElementCollector(doc)
                         .OfClass(typeof(FamilyInstance))
                         .WhereElementIsNotElementType()
                         .Cast<FamilyInstance>())
            {
                try
                {
                    if (instance.Symbol?.Family != null && instance.Symbol.Family.IsInPlace)
                        inPlace.Add(instance);
                }
                catch
                {
                    // ignore families that cannot be read
                }
            }
            if (inPlace.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ExportReadiness,
                    Severity = DmSeverity.Warning,
                    Scope = "In-place families",
                    Title = inPlace.Count + " in-place family instance(s) in the model",
                    Detail = "In-place families usually export as IfcBuildingElementProxy instead of the IFC " +
                             "class DM expects for that element, and they carry no type attributes.",
                    Reference = "DM offline self-assessment — correct IFC class per element",
                    FixKind = DmFixKind.Review,
                    FixAction = "Replace in-place families with loadable families or system families, or set " +
                                "\"Export to IFC As\" explicitly on each of them.",
                    CheckedCount = inPlace.Count,
                    AffectedCount = inPlace.Count
                }, result.ModelTitle, inPlace, options);
            }

            // Model warnings.
            IList<FailureMessage> warnings = doc.GetWarnings();
            if (warnings.Count > 20)
            {
                var elements = new List<Element>();
                foreach (FailureMessage warning in warnings.Take(200))
                {
                    foreach (ElementId id in warning.GetFailingElements())
                    {
                        Element? element = doc.GetElement(id);
                        if (element != null)
                            elements.Add(element);
                    }
                }
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ExportReadiness,
                    Severity = warnings.Count > 50 ? DmSeverity.Error : DmSeverity.Warning,
                    Scope = "Model warnings",
                    Title = warnings.Count + " Revit warnings in the model",
                    Detail = "Warnings such as overlapping elements, duplicated instances and room " +
                             "separation problems become geometry and area errors in the exported IFC.",
                    Reference = "DM offline self-assessment — geometry, duplicated elements",
                    FixKind = DmFixKind.ModelChange,
                    FixAction = "Review Manage ▸ Warnings and clear at least the overlap, duplicate and " +
                                "room-boundary warnings before export.",
                    CheckedCount = warnings.Count,
                    AffectedCount = warnings.Count
                }, result.ModelTitle, elements, options);
            }

            // The export setup itself cannot be read from the API: keep it as a checklist item.
            Add(result, new DmFinding
            {
                Group = DmCheckGroup.ExportReadiness,
                Severity = DmSeverity.Warning,
                Scope = "IFC export setup",
                Title = "Confirm the DM IFC export setup before exporting",
                Detail = "DM requires IFC4 Reference View, the Dubai property set and category mapping files, " +
                         "base quantities on, internal Revit property sets off, linked files and 2D elements " +
                         "not exported, space boundaries level 1, rooms in view exported, and Geographic " +
                         "Reference = Shared Coordinates.",
                Reference = "Technical Guides Part 5.1.4-5.1.6 — IFC export settings",
                FixKind = DmFixKind.Review,
                FixAction = "Load \"Dubai BIM E-Submission_IFC4_ReferenceView.json\" as the export setup and the " +
                            "DM property set and category mapping files, then export with Shared Coordinates.",
                CheckedCount = 1,
                AffectedCount = 0
            }, result.ModelTitle);

            Summarize(result, DmCheckGroup.ExportReadiness,
                      "File naming, imports, links, warnings and export setup", 6, before);
        }

        // ── shared helpers ──────────────────────────────────────────────────────

        private static void Add(DmAuditResult result, DmFinding finding, string modelTitle,
                                IList<Element>? elements = null, DmAuditOptions? options = null)
        {
            if (elements != null)
            {
                int max = options?.MaxElementsPerFinding ?? 20000;
                foreach (Element element in elements)
                {
                    if (finding.ElementIds.Count >= max)
                        break;
                    finding.ElementIds.Add(element.Id.Value);
                    if (finding.ElementLabels.Count < 12)
                        finding.ElementLabels.Add(Label(element));
                }
                if (finding.AffectedCount == 0)
                    finding.AffectedCount = elements.Count;
            }

            if (finding.ReferenceData.Length == 0)
                finding.ReferenceData = DmReferenceData.ForAttribute(finding.ParameterName, finding.Table);
            if (result.SpaceUsageSuggestions.Length > 0 &&
                (finding.ParameterName == "SpaceUsageCode" || finding.ParameterName == "SpaceUsageDescription"))
                finding.ReferenceData = result.SpaceUsageSuggestions + "\n" + finding.ReferenceData;
            if (finding.FixScript.Length == 0)
                finding.FixScript = DmScriptBuilder.ForFinding(finding);

            finding.McpPrompt = DmPromptBuilder.ForFinding(finding, modelTitle);
            result.Findings.Add(finding);
        }

        private static string Label(Element element)
        {
            string category = element.Category?.Name ?? "Element";
            string name;
            try
            {
                name = element.Name;
            }
            catch
            {
                name = "";
            }
            return category + " · " + (name.Length > 0 ? name : "(unnamed)") +
                   " · id " + element.Id.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static void Summarize(DmAuditResult result, DmCheckGroup group, string name,
                                      int checkedCount, int findingsBefore)
        {
            var added = result.Findings.Skip(findingsBefore).ToList();
            DmSeverity worst = DmSeverity.Pass;
            foreach (DmFinding finding in added)
            {
                if ((int)finding.Severity < (int)worst)
                    worst = finding.Severity;
            }

            result.Checks.Add(new DmCheckSummary
            {
                Group = group,
                Name = name,
                Checked = checkedCount,
                Issues = added.Count,
                Worst = worst,
                Result = added.Count == 0
                    ? "PASS"
                    : added.Count + " finding(s), worst: " + (worst == DmSeverity.Pass ? "PASS" : worst.ToString().ToUpperInvariant())
            });
        }
    }
}
