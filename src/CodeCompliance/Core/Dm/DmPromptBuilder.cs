using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// Turns a finding into a prompt the user can paste into Claude while the Revit MCP
    /// connector is running, so the fix is applied to the very elements the audit flagged.
    /// The prompts are deliberately explicit about what may and may not be changed.
    /// </summary>
    public static class DmPromptBuilder
    {
        private const int MaxIdsInPrompt = 150;

        /// <summary>How to derive a value for the DM attributes that are not simply typed in.</summary>
        private static readonly Dictionary<string, string> Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "IsExternal", "Derive it from the wall/window/door function and position: elements on the building envelope are Yes, internal ones No. Revit's wall type 'Function' (Exterior/Foundation/Retaining) is a reliable source for walls; for doors and windows use the host wall's IsExternal." },
            { "FireRating", "Use the fire rating already on the type (Revit's built-in 'Fire Rating' type parameter) and express it as the rating in minutes, e.g. 60, 90, 120. Never invent a rating: list the types with no rating and ask me instead." },
            { "LoadBearing", "Structural walls, columns, slabs and foundations are Yes. In Revit the wall instance parameter 'Structural' and the structural category already say this." },
            { "ThermalTransmittance", "Read the U-value of the type's compound structure (Revit computes 'Thermal Mass' / analytic properties). Where Revit has no value, list the types and ask me for the design U-value rather than guessing." },
            { "IfcMaterial", "Use the dominant material of the element type's compound structure or the structural material parameter, as plain text, e.g. Concrete, Blockwork, Aluminium, Timber." },
            { "CompressiveStrength", "Use the concrete grade of the structural material (e.g. 40 for C40), as a number in the unit the DM standard asks for. Ask me for grades that are not in the model." },
            { "SpaceUsageCode", "Map each room name to a DM Space Usage code from Appendix C (SC_xx_xx_xx). The plugin ships the code list at Documents\\CodeCompliance\\DMKnowledgeBase\\usage_Space.csv; match on the room name, propose the mapping as a table and only write it after I confirm." },
            { "SpaceUsageDescription", "Must be the Appendix C description belonging to the SpaceUsageCode of the same room, copied verbatim from the code list." },
            { "UnitUsageCode", "Only for spaces that are part of a unit (apartment, villa, shop, office). Use the Appendix C unit codes (RE_xx, CO_xx, ...) and leave spaces that belong to no unit empty." },
            { "UnitUsageDescription", "Must match the UnitUsageCode of the same space, copied from the Appendix C unit list." },
            { "UnitNo", "The unit identifier the space belongs to, consistent with the unit numbering on the submitted drawings." },
            { "UnitExtraInfo", "Formatted list of the extra info the unit type requires, e.g. [IsAreaLessThan150:N],[Persons:4],[WCs:2]." },
            { "BuildingOccupancyUsageCode", "The building occupancy code from Appendix C (e.g. MS_01_01) — the same value for all spaces of one building occupancy." },
            { "ZoneName", "The zone the space belongs to, per Appendix C zone categories (Amenities, Circulation Areas, Hygiene Areas, Living Space, ...)." },
            { "ZoneObjectType", "The Appendix C ZoneObjectType belonging to the zone: Amenity, CirculationArea, HygieneArea, LivingSpace, OtherArea, RetailingArea, TechnicalService." },
            { "DoorClearWidth", "The clear opening width in metres per Dubai Building Code figure B.43 — for a single leaf door that is the leaf width minus the door stop, not the rough opening." },
            { "Operation", "Door/window operation type, e.g. SINGLE_SWING_LEFT, DOUBLE_SWING, SLIDING — take it from the family type." },
            { "IsEntrance", "Yes only for doors that are a building or unit entrance." },
            { "HandicapAccessible", "Yes for accessible doors/spaces per the accessibility design (clear width, approach). Ask me where the design intent is not readable from the model." },
            { "PredefinedType", "Use one of the IFC4 enumerations DM lists for this class; when none fits, set PredefinedType to USERDEFINED and put the custom value in ObjectTypeOverride." },
            { "ObjectTypeOverride", "The DM custom sub-type for this element, from the Appendix B 'object type override data samples' list of this element class." },
            { "TotalBuildupArea", "The built-up area of the level/building in square metres, matching the area calculations submitted with the permit drawings (±5% of DM's automated calculation)." },
            { "TotalGrossArea", "The gross area in square metres, consistent with the submitted area statement." },
            { "TotalFloorGrossArea", "The gross floor area in square metres, consistent with the submitted area statement." },
            { "TotalNetArea", "The net area in square metres — the sum of the spaces flagged IsOccupiedSpace or IsHabitableSpace." },
            { "GateLevel", "The DMD elevation of the GA_GATE LEVEL level, in metres, exactly as used when the coordinates were acquired from the parcel DXF." },
            { "ParcelId", "The parcel id from the affection plan. It must be identical to the PA field of the IFC file name." },
            { "BIMStandardVersion", "The Dubai BIM Standard version the model is prepared against — currently " + DmKnowledgeBase.StandardVersion + "." }
        };

        /// <summary>Prompt that fixes one finding.</summary>
        public static string ForFinding(DmFinding finding, string modelTitle)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Revit MCP task — Dubai Municipality BIM compliance fix");
            sb.AppendLine("Model: " + modelTitle);
            sb.AppendLine("Finding: [" + finding.SeverityText + "] " + finding.Title);
            if (finding.Reference.Length > 0)
                sb.AppendLine("DM reference: " + finding.Reference);
            sb.AppendLine();

            sb.AppendLine("What is wrong");
            sb.AppendLine(finding.Detail);
            sb.AppendLine();

            sb.AppendLine("What to change (" + finding.FixKindText.ToLowerInvariant() + ")");
            sb.AppendLine(finding.FixAction);
            if (finding.ParameterName.Length > 0)
            {
                sb.AppendLine("Parameter: " + finding.ParameterName +
                              (finding.SampleValue.Length > 0 ? "   ·   DM data sample: " + finding.SampleValue : ""));
                string hint = Hint(finding.ParameterName);
                if (hint.Length > 0)
                {
                    sb.AppendLine("How to derive the value: " + hint);
                }
            }
            sb.AppendLine();

            if (finding.HasElements)
            {
                sb.AppendLine("Affected elements (" + finding.AffectedCount.ToString(CultureInfo.InvariantCulture) +
                              " of " + finding.CheckedCount.ToString(CultureInfo.InvariantCulture) + " checked)");
                sb.AppendLine("Element ids: " + IdList(finding.ElementIds));
                sb.AppendLine();
            }

            sb.AppendLine("How to work");
            sb.AppendLine("1. Use the revit-mcp connection to the model that is already open in Revit " +
                          "(APG Revit Plugins ▸ Revit MCP ▸ MCP Server must be running).");
            sb.AppendLine("2. Read the current state of the listed elements before changing anything and " +
                          "show me a table of what you intend to write.");
            sb.AppendLine("3. Apply the change in a single transaction named \"DM compliance – " +
                          Compact(finding.Title) + "\" so I can undo it in one step.");
            sb.AppendLine("4. Do not move, delete or re-host geometry, and do not touch elements outside the list.");
            sb.AppendLine("5. When a value cannot be derived from the model, list those elements and ask me " +
                          "instead of writing a guess.");
            sb.AppendLine("6. Finish with a short report: how many elements were changed, which were skipped and why.");
            return sb.ToString();
        }

        /// <summary>Prompt that walks the whole audit, worst findings first.</summary>
        public static string ForAudit(DmAuditResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Revit MCP task — fix the Dubai Municipality BIM compliance findings of this model");
            sb.AppendLine("Model: " + result.ModelTitle + "   ·   permit stage: " +
                          (result.Stage == DmPermitStage.Final ? "Final" : "Preliminary") +
                          "   ·   Dubai BIM Standard " + DmKnowledgeBase.StandardVersion);
            sb.AppendLine("Audit run by APG Revit Plugins ▸ DM BIM Compliance on " +
                          result.RunAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("Findings: " + result.Count(DmSeverity.Critical) + " critical, " +
                          result.Count(DmSeverity.Error) + " errors, " +
                          result.Count(DmSeverity.Warning) + " warnings, over " +
                          result.AffectedElements + " elements.");
            sb.AppendLine();
            sb.AppendLine("Work through them in this order, one finding at a time, and stop after each one " +
                          "for my confirmation before writing to the model:");
            sb.AppendLine();

            int index = 1;
            foreach (DmFinding finding in result.Findings
                         .Where(f => f.Severity != DmSeverity.Pass)
                         .OrderBy(f => (int)f.Severity)
                         .ThenBy(f => (int)f.Group))
            {
                sb.AppendLine(index.ToString(CultureInfo.InvariantCulture) + ". [" + finding.SeverityText + "] " +
                              finding.Scope + " — " + finding.Title);
                sb.AppendLine("   Fix (" + finding.FixKindText.ToLowerInvariant() + "): " + finding.FixAction);
                if (finding.HasElements)
                    sb.AppendLine("   Elements: " + finding.AffectedCount + " affected, ids " + IdList(finding.ElementIds, 40));
                index++;
            }

            sb.AppendLine();
            sb.AppendLine("Rules: never change geometry, never delete elements without asking, one Revit " +
                          "transaction per finding, and report what you changed after each step. Values you " +
                          "cannot derive from the model must be asked, not guessed.");
            return sb.ToString();
        }

        /// <summary>The DM-specific hint for a parameter, or "" when there is none.</summary>
        public static string Hint(string parameterName)
        {
            return Hints.TryGetValue(parameterName, out string? hint) ? hint : "";
        }

        private static string IdList(IList<long> ids, int max = MaxIdsInPrompt)
        {
            if (ids.Count == 0)
                return "(none)";
            IEnumerable<long> shown = ids.Take(max);
            string text = string.Join(", ", shown.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            if (ids.Count > max)
                text += " … and " + (ids.Count - max).ToString(CultureInfo.InvariantCulture) +
                        " more (the full list is in the exported CSV report)";
            return text;
        }

        private static string Compact(string title)
        {
            string text = title.Length > 48 ? title.Substring(0, 48) : title;
            return text.Replace("\"", "'");
        }
    }
}
