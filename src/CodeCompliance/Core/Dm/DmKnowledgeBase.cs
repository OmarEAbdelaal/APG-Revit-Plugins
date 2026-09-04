using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace CodeCompliance.Core.Dm
{
    /// <summary>One machine-checkable rule of Dubai Municipality's official IDS file.</summary>
    public sealed class DmIdsRule
    {
        public string Name { get; set; } = "";
        public string Instructions { get; set; } = "";
        public List<string> Entities { get; } = new List<string>();
        public List<DmIdsRequirement> Requirements { get; } = new List<DmIdsRequirement>();
    }

    /// <summary>A single requirement inside an IDS rule (a property, or the entity itself).</summary>
    public sealed class DmIdsRequirement
    {
        public string Type { get; set; } = "";
        public string Cardinality { get; set; } = "";
        public string DataType { get; set; } = "";
        public string PropertySet { get; set; } = "";
        public string BaseName { get; set; } = "";

        public bool IsProperty => string.Equals(Type, "property", StringComparison.OrdinalIgnoreCase);
        public bool IsRequired => string.Equals(Cardinality, "required", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One row of an Appendix B element attribute table: the attribute an element type must
    /// carry, the property set it lands in, and whether it is required per permit stage.
    /// </summary>
    public sealed class DmAttribute
    {
        public string Name { get; set; } = "";
        public string PropertySet { get; set; } = "";
        public string Description { get; set; } = "";
        public bool UserInput { get; set; }
        public string DataType { get; set; } = "";
        public string Sample { get; set; } = "";
        public string Preliminary { get; set; } = "";
        public string Final { get; set; } = "";

        /// <summary>"Required", "Conditional" or "-" for the requested permit stage.</summary>
        public string RequirementFor(DmPermitStage stage)
        {
            return stage == DmPermitStage.Preliminary ? Preliminary : Final;
        }

        public bool IsRequiredFor(DmPermitStage stage)
        {
            return RequirementFor(stage).StartsWith("Required", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsConditionalFor(DmPermitStage stage)
        {
            return RequirementFor(stage).StartsWith("Conditional", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Building permit stage the model is being checked for.</summary>
    public enum DmPermitStage
    {
        Preliminary,
        Final
    }

    /// <summary>A controlled-vocabulary entry from Appendix C (usage codes).</summary>
    public sealed class DmUsageCode
    {
        public string Code { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public string Extra { get; set; } = "";
    }

    /// <summary>
    /// The Dubai Municipality BIM compliance knowledge base, shipped with the plugin as
    /// embedded data (never hardcoded in C#): DM's own IDS rule set, the Appendix B element
    /// attribute tables, the Appendix C usage-code vocabularies, the Revit category to IFC
    /// class mapping and the list of DM shared parameters.
    ///
    /// DM revises the standard every few months, so the files can be overridden without a new
    /// plugin build: drop updated files in <see cref="OverrideFolder"/> and they win over the
    /// embedded copies (same file names).
    /// </summary>
    public static class DmKnowledgeBase
    {
        private const string ResourcePrefix = "CodeCompliance.Resources.DmKnowledgeBase.";

        /// <summary>Dubai BIM Standard version this knowledge base was extracted from.</summary>
        public const string StandardVersion = "1.4";

        private static readonly object Gate = new object();
        private static bool _loaded;

        private static List<DmIdsRule> _idsRules = new List<DmIdsRule>();
        private static Dictionary<string, List<string>> _categoryToIfc = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _sharedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, List<DmAttribute>> _attributes = new Dictionary<string, List<DmAttribute>>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, DmUsageCode> _spaceUsage = new Dictionary<string, DmUsageCode>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, DmUsageCode> _unitUsage = new Dictionary<string, DmUsageCode>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, DmUsageCode> _buildingUsage = new Dictionary<string, DmUsageCode>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, DmUsageCode> _zoneObjectTypes = new Dictionary<string, DmUsageCode>(StringComparer.OrdinalIgnoreCase);
        private static List<string> _unitExtraInfoKeys = new List<string>();
        private static string _source = "embedded";

        /// <summary>Folder the user can drop updated DM data files into (they override the embedded ones).</summary>
        public static string OverrideFolder =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "CodeCompliance", "DMKnowledgeBase");

        public static IReadOnlyList<DmIdsRule> IdsRules { get { EnsureLoaded(); return _idsRules; } }
        public static IReadOnlyDictionary<string, List<string>> CategoryToIfc { get { EnsureLoaded(); return _categoryToIfc; } }
        public static IReadOnlyDictionary<string, string> SharedParameters { get { EnsureLoaded(); return _sharedParameters; } }
        public static IReadOnlyDictionary<string, DmUsageCode> SpaceUsageCodes { get { EnsureLoaded(); return _spaceUsage; } }
        public static IReadOnlyDictionary<string, DmUsageCode> UnitUsageCodes { get { EnsureLoaded(); return _unitUsage; } }
        public static IReadOnlyDictionary<string, DmUsageCode> BuildingOccupancyCodes { get { EnsureLoaded(); return _buildingUsage; } }
        public static IReadOnlyDictionary<string, DmUsageCode> ZoneObjectTypes { get { EnsureLoaded(); return _zoneObjectTypes; } }
        public static IReadOnlyList<string> UnitExtraInfoKeys { get { EnsureLoaded(); return _unitExtraInfoKeys; } }

        /// <summary>"embedded" or the override folder the data was actually read from.</summary>
        public static string Source { get { EnsureLoaded(); return _source; } }

        /// <summary>
        /// Appendix B attributes for one element table, e.g. "Wall", "Door", "Room_Spaces".
        /// Empty list when the table is unknown.
        /// </summary>
        public static IReadOnlyList<DmAttribute> Attributes(string table)
        {
            EnsureLoaded();
            return _attributes.TryGetValue(table, out List<DmAttribute>? list) ? list : new List<DmAttribute>();
        }

        /// <summary>Attributes of a table the modeller must fill in (User Input = YES) for a stage.</summary>
        public static List<DmAttribute> RequiredAttributes(string table, DmPermitStage stage, bool includeConditional)
        {
            return Attributes(table)
                .Where(a => a.UserInput && (a.IsRequiredFor(stage) || (includeConditional && a.IsConditionalFor(stage))))
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>IDS property requirements that apply to one IFC entity, e.g. "IFCWALL".</summary>
        public static List<DmIdsRequirement> IdsPropertiesFor(string ifcEntity)
        {
            EnsureLoaded();
            var result = new List<DmIdsRequirement>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DmIdsRule rule in _idsRules)
            {
                if (!rule.Entities.Any(e => string.Equals(e, ifcEntity, StringComparison.OrdinalIgnoreCase)))
                    continue;
                foreach (DmIdsRequirement req in rule.Requirements)
                {
                    if (!req.IsProperty || string.IsNullOrEmpty(req.BaseName))
                        continue;
                    if (seen.Add(req.BaseName))
                        result.Add(req);
                }
            }
            return result;
        }

        /// <summary>Forces a reload on the next access (used after the user edits the override folder).</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _loaded = false;
            }
        }

        /// <summary>
        /// Writes the embedded DM data files into <see cref="OverrideFolder"/> so the user can
        /// update them when Dubai Municipality revises the standard. Existing files are kept.
        /// </summary>
        public static int ExportEmbedded(out string folder)
        {
            folder = OverrideFolder;
            Directory.CreateDirectory(folder);
            int written = 0;
            foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                    continue;
                string fileName = resource.Substring(ResourcePrefix.Length);
                string path = Path.Combine(folder, fileName);
                if (File.Exists(path))
                    continue;
                using (Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                {
                    if (stream == null)
                        continue;
                    using (FileStream file = File.Create(path))
                        stream.CopyTo(file);
                }
                written++;
            }
            Reset();
            return written;
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;
            lock (Gate)
            {
                if (_loaded)
                    return;
                try
                {
                    Load();
                }
                catch
                {
                    // A damaged data file must never take the plugin down: the audit then simply
                    // reports fewer checks.
                }
                _loaded = true;
            }
        }

        private static void Load()
        {
            _idsRules = new List<DmIdsRule>();
            _categoryToIfc = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _sharedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _attributes = new Dictionary<string, List<DmAttribute>>(StringComparer.OrdinalIgnoreCase);
            _spaceUsage = new Dictionary<string, DmUsageCode>(StringComparer.OrdinalIgnoreCase);
            _unitUsage = new Dictionary<string, DmUsageCode>(StringComparer.OrdinalIgnoreCase);
            _buildingUsage = new Dictionary<string, DmUsageCode>(StringComparer.OrdinalIgnoreCase);
            _zoneObjectTypes = new Dictionary<string, DmUsageCode>(StringComparer.OrdinalIgnoreCase);
            _unitExtraInfoKeys = new List<string>();
            _source = Directory.Exists(OverrideFolder) ? OverrideFolder : "embedded";

            LoadIdsRules(ReadFile("ids_rules.json"));
            LoadCategoryMap(ReadFile("category_to_ifc.json"));
            LoadSharedParameters(ReadFile("shared_parameters.json"));
            LoadSpaceUsage(ReadFile("usage_Space.csv"));
            LoadUnitUsage(ReadFile("usage_Unit.csv"));
            LoadBuildingUsage(ReadFile("usage_Building.csv"));
            LoadZones(ReadFile("usage_Zone.csv"));
            LoadUnitExtraInfo(ReadFile("usage_Unit_Extra_Info.csv"));

            foreach (string file in ListFiles())
            {
                if (!file.StartsWith("attr_", StringComparison.OrdinalIgnoreCase) ||
                    !file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    continue;
                string table = file.Substring("attr_".Length, file.Length - "attr_".Length - ".csv".Length);
                List<DmAttribute> attributes = ParseAttributeTable(ReadFile(file));
                if (attributes.Count > 0)
                    _attributes[table] = attributes;
            }
        }

        // ── file access (override folder first, then embedded resources) ────────

        private static IEnumerable<string> ListFiles()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if (resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                    names.Add(resource.Substring(ResourcePrefix.Length));
            }
            try
            {
                if (Directory.Exists(OverrideFolder))
                {
                    foreach (string path in Directory.GetFiles(OverrideFolder))
                        names.Add(Path.GetFileName(path));
                }
            }
            catch
            {
                // unreadable override folder: embedded data only
            }
            return names;
        }

        private static string ReadFile(string fileName)
        {
            try
            {
                string overridePath = Path.Combine(OverrideFolder, fileName);
                if (File.Exists(overridePath))
                    return File.ReadAllText(overridePath);
            }
            catch
            {
                // fall through to the embedded copy
            }

            using (Stream? stream = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream(ResourcePrefix + fileName))
            {
                if (stream == null)
                    return "";
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
        }

        // ── parsers ─────────────────────────────────────────────────────────────

        private static void LoadIdsRules(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;
            var array = JArray.Parse(json);
            foreach (JToken token in array)
            {
                var rule = new DmIdsRule
                {
                    Name = (string?)token["name"] ?? "",
                    Instructions = (string?)token["instructions"] ?? ""
                };
                if (token["entities"] is JArray entities)
                {
                    foreach (JToken entity in entities)
                        rule.Entities.Add(((string?)entity ?? "").Trim());
                }
                if (token["requirements"] is JArray requirements)
                {
                    foreach (JToken requirement in requirements)
                    {
                        rule.Requirements.Add(new DmIdsRequirement
                        {
                            Type = (string?)requirement["type"] ?? "",
                            Cardinality = (string?)requirement["cardinality"] ?? "",
                            DataType = (string?)requirement["dataType"] ?? "",
                            PropertySet = (string?)requirement["propertySet"] ?? "",
                            BaseName = (string?)requirement["baseName"] ?? ""
                        });
                    }
                }
                _idsRules.Add(rule);
            }
        }

        private static void LoadCategoryMap(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;
            var obj = JObject.Parse(json);
            foreach (JProperty property in obj.Properties())
            {
                var classes = new List<string>();
                if (property.Value is JArray array)
                {
                    foreach (JToken value in array)
                        classes.Add(((string?)value ?? "").Trim());
                }
                _categoryToIfc[property.Name] = classes;
            }
        }

        private static void LoadSharedParameters(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;
            var array = JArray.Parse(json);
            foreach (JToken token in array)
            {
                string name = (string?)token["name"] ?? "";
                if (name.Length == 0)
                    continue;
                _sharedParameters[name] = (string?)token["dataType"] ?? "";
            }
        }

        /// <summary>
        /// Appendix B tables have two or three title rows, then a header row starting with
        /// "Attribute", then one row per attribute.
        /// </summary>
        private static List<DmAttribute> ParseAttributeTable(string csv)
        {
            var result = new List<DmAttribute>();
            List<List<string>> rows = DmCsv.Parse(csv);
            int header = rows.FindIndex(r => r.Count > 0 &&
                                             string.Equals(r[0].Trim(), "Attribute", StringComparison.OrdinalIgnoreCase));
            if (header < 0)
                return result;

            List<string> columns = rows[header];
            int iSet = IndexOf(columns, "Property Set");
            int iDescription = IndexOf(columns, "Description");
            int iUserInput = IndexOf(columns, "User Input (YES/NO)");
            int iDataType = IndexOf(columns, "Data Type");
            int iSample = IndexOf(columns, "Data Sample");
            int iPreliminary = IndexOf(columns, "Preliminary Permit");
            int iFinal = IndexOf(columns, "Final Permit");

            for (int i = header + 1; i < rows.Count; i++)
            {
                List<string> row = rows[i];
                if (DmCsv.IsEmpty(row))
                    continue;
                string name = DmCsv.Cell(row, 0);
                if (name.Length == 0)
                    continue;

                result.Add(new DmAttribute
                {
                    Name = Normalize(name),
                    PropertySet = DmCsv.Cell(row, iSet),
                    Description = DmCsv.Cell(row, iDescription),
                    UserInput = DmCsv.Cell(row, iUserInput).StartsWith("Y", StringComparison.OrdinalIgnoreCase),
                    DataType = DmCsv.Cell(row, iDataType),
                    Sample = DmCsv.Cell(row, iSample),
                    Preliminary = DmCsv.Cell(row, iPreliminary),
                    Final = DmCsv.Cell(row, iFinal)
                });
            }
            return result;
        }

        /// <summary>
        /// Corrects the typos DM carries in its own published attribute tables, so the checker
        /// looks for the parameter name the Revit template actually uses.
        /// </summary>
        private static string Normalize(string attribute)
        {
            switch (attribute)
            {
                case "Staus": return "Status";
                case "Hight": return "Height";
                case "NetHeigtht": return "NetHeight";
                case "Fire Compartment": return "Compartmentation";
                case "GlazedAreaFraction": return "GlazingAreaFraction";
                default: return attribute;
            }
        }

        private static int IndexOf(IList<string> header, string column)
        {
            for (int i = 0; i < header.Count; i++)
            {
                if (string.Equals(header[i].Trim(), column, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        // Code,Zone Category,Space Description,Space Category Codes,...
        private static void LoadSpaceUsage(string csv)
        {
            foreach (List<string> row in DataRows(csv, "Code"))
            {
                string code = DmCsv.Cell(row, 3);
                if (code.Length == 0)
                    continue;
                _spaceUsage[code] = new DmUsageCode
                {
                    Code = code,
                    Description = DmCsv.Cell(row, 2),
                    Category = DmCsv.Cell(row, 1)
                };
            }
        }

        // Building Master Usage,Unit,UnitUsageCode,Required Extra Info,...
        private static void LoadUnitUsage(string csv)
        {
            foreach (List<string> row in DataRows(csv, "Building Master Usage"))
            {
                string code = DmCsv.Cell(row, 2);
                if (code.Length == 0)
                    continue;
                _unitUsage[code] = new DmUsageCode
                {
                    Code = code,
                    Description = DmCsv.Cell(row, 1),
                    Category = DmCsv.Cell(row, 0),
                    Extra = DmCsv.Cell(row, 3)
                };
            }
        }

        // Building Type,Master Usage (Occupancy),Main Usage (OccupancyUse),OccupancyUsageCode,...
        private static void LoadBuildingUsage(string csv)
        {
            foreach (List<string> row in DataRows(csv, "Building Type"))
            {
                string code = DmCsv.Cell(row, 3);
                if (code.Length == 0)
                    continue;
                _buildingUsage[code] = new DmUsageCode
                {
                    Code = code,
                    Description = DmCsv.Cell(row, 2),
                    Category = DmCsv.Cell(row, 1),
                    Extra = DmCsv.Cell(row, 0)
                };
            }
        }

        // Zone Category,Zone Code,ZoneObjectType
        private static void LoadZones(string csv)
        {
            foreach (List<string> row in DataRows(csv, "Zone Category"))
            {
                string objectType = DmCsv.Cell(row, 2);
                if (objectType.Length == 0)
                    continue;
                _zoneObjectTypes[objectType] = new DmUsageCode
                {
                    Code = DmCsv.Cell(row, 1),
                    Description = objectType,
                    Category = DmCsv.Cell(row, 0)
                };
            }
        }

        // Name,Desc.,Data Type
        private static void LoadUnitExtraInfo(string csv)
        {
            foreach (List<string> row in DataRows(csv, "Name"))
            {
                string name = DmCsv.Cell(row, 0);
                if (name.Length > 0)
                    _unitExtraInfoKeys.Add(name);
            }
        }

        /// <summary>Rows of a usage-code CSV after the header row that starts with <paramref name="firstColumn"/>.</summary>
        private static IEnumerable<List<string>> DataRows(string csv, string firstColumn)
        {
            List<List<string>> rows = DmCsv.Parse(csv);
            int header = rows.FindIndex(r => r.Count > 0 &&
                                             string.Equals(r[0].Trim(), firstColumn, StringComparison.OrdinalIgnoreCase));
            if (header < 0)
                yield break;
            for (int i = header + 1; i < rows.Count; i++)
            {
                if (!DmCsv.IsEmpty(rows[i]))
                    yield return rows[i];
            }
        }
    }
}
