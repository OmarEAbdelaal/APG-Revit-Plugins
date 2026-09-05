using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// Assembles the Dubai Municipality data a fix needs — data types, allowed values,
    /// controlled vocabularies, shared parameter definitions and naming tables — out of the
    /// knowledge base the plugin ships. It travels inside the prompt so neither the user nor
    /// Claude has to find, download or upload a DM file.
    /// </summary>
    public static class DmReferenceData
    {
        private const int MaxVocabularyEntries = 120;

        /// <summary>Everything known about the attribute a finding is about.</summary>
        public static string ForAttribute(string parameterName, string table)
        {
            if (parameterName.Length == 0)
                return "";

            var sb = new StringBuilder();

            if (DmKnowledgeBase.SharedParameters.TryGetValue(parameterName, out string? dataType))
            {
                sb.AppendLine("Shared parameter: " + parameterName +
                              "   ·   data type " + DmSharedParameters.DataType(dataType) +
                              "   ·   group \"" + DmSharedParameters.GroupName + "\"" +
                              "   ·   GUID " + DmSharedParameters.GuidFor(parameterName));
            }

            DmAttribute? attribute = table.Length > 0
                ? DmKnowledgeBase.Attributes(table)
                    .FirstOrDefault(a => string.Equals(a.Name, parameterName, StringComparison.OrdinalIgnoreCase))
                : null;
            if (attribute != null)
            {
                if (attribute.PropertySet.Length > 0)
                    sb.AppendLine("Property set: " + attribute.PropertySet);
                if (attribute.DataType.Length > 0)
                    sb.AppendLine("DM data type: " + attribute.DataType);
                if (attribute.Sample.Length > 0)
                    sb.AppendLine("DM data sample: " + attribute.Sample);
                if (attribute.Description.Length > 0)
                    sb.AppendLine("DM definition: " + attribute.Description);
            }

            if (parameterName.Equals("PredefinedType", StringComparison.OrdinalIgnoreCase) && table.Length > 0)
            {
                string values = DmKnowledgeBase.PredefinedTypes(table);
                if (values.Length > 0)
                    sb.AppendLine("Allowed IFC4 PredefinedType values: " + values +
                                  "  (use USERDEFINED and fill ObjectTypeOverride when none fits)");
            }

            if (parameterName.Equals("ObjectTypeOverride", StringComparison.OrdinalIgnoreCase) && table.Length > 0)
            {
                string values = DmKnowledgeBase.ObjectTypeOverrides(table);
                if (values.Length > 0)
                    sb.AppendLine("DM ObjectTypeOverride values for this class: " + values);
            }

            sb.Append(Vocabulary(parameterName));
            return sb.ToString();
        }

        /// <summary>The controlled vocabulary a code attribute has to come from.</summary>
        public static string Vocabulary(string parameterName)
        {
            switch (parameterName)
            {
                case "ZoneObjectType":
                    return List("Appendix C zone categories (ZoneObjectType — zone code — category)",
                        DmKnowledgeBase.ZoneObjectTypes.Values
                            .Select(z => z.Description + " — " + z.Code + " — " + z.Category));
                case "ZoneName":
                    return List("Appendix C zone categories the ZoneName has to belong to",
                        DmKnowledgeBase.ZoneObjectTypes.Values.Select(z => z.Category + " (" + z.Code + ")"));
                case "UnitUsageCode":
                case "UnitUsageDescription":
                    return List("Appendix C unit usage codes (code — unit — building master usage)",
                        DmKnowledgeBase.UnitUsageCodes.Values
                            .Select(u => u.Code + " — " + u.Description + " — " + u.Category));
                case "BuildingOccupancyUsageCode":
                case "BuildingOccupancyUsageDescription":
                case "OccupancyUsageCode":
                    return List("Appendix C building occupancy usage codes (code — main usage — master usage)",
                        DmKnowledgeBase.BuildingOccupancyCodes.Values
                            .Select(b => b.Code + " — " + b.Description + " — " + b.Category));
                case "UnitExtraInfo":
                    return List("Appendix C unit extra info keys",
                        DmKnowledgeBase.UnitExtraInfoKeys);
                case "SpaceUsageCode":
                case "SpaceUsageDescription":
                    return "Appendix C space usage codes: " + DmKnowledgeBase.SpaceUsageCodes.Count +
                           " codes are shipped with the plugin; the suggested mapping for the rooms of this " +
                           "model is listed above. The full list is in " +
                           DmKnowledgeBase.OverrideFolder + "\\usage_Space.csv.\n";
                default:
                    return "";
            }
        }

        /// <summary>The level naming table, for level findings.</summary>
        public static string LevelNaming()
        {
            return "DM level naming: <ABBREVIATION>_<IDENTIFICATION>, uppercase.\n" +
                   "  B1_BASEMENT1, B2_BASEMENT2   RD_ROADLEVEL   GA_GATE LEVEL   GR_GROUND FLOOR\n" +
                   "  P1_PODIUM1   M1_MEZZANINE1   F1_FLOOR1, F2_FLOOR2 …   S1_SERVICE1   RF_ROOF\n" +
                   "The architectural and structural models of one building must use identical level names.\n";
        }

        /// <summary>The object naming rule, for family and type naming findings.</summary>
        public static string ObjectNaming()
        {
            return "DM object naming: CATEGORY_FUNCTIONALTYPE_DISCIPLINE_DESCRIPTION1_DESCRIPTION2\n" +
                   "  · at most 30 characters      · no spaces, underscores separate the fields\n" +
                   "  · only letters, digits and underscores   · abbreviations uppercase\n" +
                   "  example: DOR_INT_AR_850x2100_TIMBER\n";
        }

        /// <summary>The file naming rule, for submission findings.</summary>
        public static string FileNaming()
        {
            return "DM file naming: PN<6 digits>_BI<6 digits>_PA<7-8 digits>_<AR|ST>_<3 digits>\n" +
                   "  example: PN123456_BI123456_PA1234567_AR_001\n" +
                   "  The PA field must equal the ParcelId attribute inside the file.\n";
        }

        private static string List(string title, IEnumerable<string> values)
        {
            List<string> items = values.Distinct().ToList();
            var sb = new StringBuilder();
            sb.AppendLine(title + " (" + items.Count + " entries, shipped with the plugin):");
            foreach (string item in items.Take(MaxVocabularyEntries))
                sb.AppendLine("  " + item);
            if (items.Count > MaxVocabularyEntries)
                sb.AppendLine("  … " + (items.Count - MaxVocabularyEntries) +
                              " more in " + DmKnowledgeBase.OverrideFolder);
            return sb.ToString();
        }
    }
}
