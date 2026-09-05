using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core.Dm
{
    /// <summary>What applying a fix did to the model.</summary>
    public sealed class DmFixOutcome
    {
        /// <summary>False when the finding is not something the plugin may fix on its own.</summary>
        public bool Attempted { get; set; }

        /// <summary>Elements (or types, or levels) actually changed.</summary>
        public int Changed { get; set; }

        /// <summary>Elements the fix deliberately left alone, with the reason.</summary>
        public List<string> Skipped { get; } = new List<string>();

        /// <summary>What to show in the dashboard status line.</summary>
        public string Message { get; set; } = "";

        public bool ChangedAnything => Changed > 0;

        /// <summary>Standard closing sentence: what changed, what did not, and why.</summary>
        public string Summarize(string what)
        {
            string text = what + ": " + Changed.ToString(CultureInfo.InvariantCulture) + " changed";
            if (Skipped.Count > 0)
            {
                text += ", " + Skipped.Count.ToString(CultureInfo.InvariantCulture) + " skipped — " +
                        string.Join("; ", Skipped.Take(6));
                if (Skipped.Count > 6)
                    text += " …";
            }
            return text + ".";
        }
    }

    /// <summary>
    /// Applies a finding's fix to the open model directly, without Claude and without the MCP
    /// link: the audit already knows exactly which elements are wrong and what the right value
    /// is, so the same change the fix script describes is made here in native Revit API calls.
    ///
    /// Three rules govern everything in this file:
    /// <list type="bullet">
    /// <item>Nothing is ever deleted, and no value is invented. A DM "data sample" is an
    /// example, not a value for this project, so an attribute whose value cannot be derived
    /// from the model is reported as skipped rather than filled in.</item>
    /// <item>One named transaction per fix, so a single Ctrl+Z puts the model back.</item>
    /// <item>Findings that need a human decision (renaming, splitting columns, remodelling,
    /// deleting redundant rooms, purging, resolving a clash) are refused here — their prompt
    /// explains the change instead.</item>
    /// </list>
    ///
    /// Runs in a Revit API context: the dashboard calls it through <see cref="DmRevitTask"/>.
    /// </summary>
    public static partial class DmFixService
    {
        /// <summary>Whether the plugin may apply this finding itself.</summary>
        public static bool CanFix(DmFinding finding)
        {
            if (finding.PracticeId.Length > 0 && Target(finding).Length > 0)
                return CanFixPractice(Target(finding));

            switch (finding.FixKind)
            {
                case DmFixKind.BindParameter:
                    return finding.ParametersToBind.Count > 0 && finding.Categories.Count > 0;
                case DmFixKind.SetParameter:
                    return finding.HasElements && finding.ParameterName.Length > 0 &&
                           Derivable(finding.ParameterName);
                default:
                    return false;
            }
        }

        /// <summary>
        /// One sentence describing what <see cref="Apply"/> would change, for the confirmation
        /// the dashboard shows before touching the model.
        /// </summary>
        public static string Describe(DmFinding finding)
        {
            if (!CanFix(finding))
                return WhyNot(finding);

            string target = Target(finding);
            if (target.Length > 0)
                return DescribePractice(finding, target);

            switch (finding.FixKind)
            {
                case DmFixKind.BindParameter:
                    return "Create " + finding.ParametersToBind.Count + " DM shared parameter(s) and bind them to " +
                           string.Join(", ", finding.Categories.Take(4)) +
                           (finding.Categories.Count > 4 ? " …" : "") + ".";
                case DmFixKind.SetParameter:
                    return "Fill \"" + finding.ParameterName + "\" on " + finding.ElementIds.Count +
                           " element(s), deriving the value from the model. Elements whose value cannot be " +
                           "derived are left untouched.";
                default:
                    return "";
            }
        }

        /// <summary>Why a finding cannot be applied automatically, in the modeller's words.</summary>
        public static string WhyNot(DmFinding finding)
        {
            string target = Target(finding);
            if (target.Length > 0)
                return WhyNotPractice(target);

            switch (finding.FixKind)
            {
                case DmFixKind.Rename:
                    return "Renaming needs the names you want — the audit knows the rule, not the name for each " +
                           "element. Use the prompt, or rename in Revit.";
                case DmFixKind.ProjectSetup:
                    return "This is a project setting (units, coordinates, export setup) and has to be changed in " +
                           "the Revit user interface.";
                case DmFixKind.Review:
                    return "This finding needs a decision before anything is changed. The prompt explains it.";
                case DmFixKind.SetParameter when !Derivable(finding.ParameterName):
                    return "\"" + finding.ParameterName + "\" cannot be derived from the model, and the DM data " +
                           "sample is an example rather than this project's value — filling it in automatically " +
                           "would invent submission data. Enter it in Revit, or use the prompt.";
                case DmFixKind.SetParameter when !finding.HasElements:
                    return "This finding is not tied to specific elements.";
                default:
                    return "This finding has no automatic fix.";
            }
        }

        /// <summary>
        /// Applies the fix. Opens and commits its own transaction, so it must run in a Revit
        /// API context and not inside another transaction.
        /// </summary>
        public static DmFixOutcome Apply(Document doc, DmFinding finding, DmPermitStage stage, bool includeConditional)
        {
            var outcome = new DmFixOutcome();
            if (!CanFix(finding))
            {
                outcome.Message = WhyNot(finding);
                return outcome;
            }

            outcome.Attempted = true;
            string name = "DM compliance – " + (finding.PracticeId.Length > 0 ? finding.PracticeId + " " : "") + "fix";

            using (var transaction = new Transaction(doc, name))
            {
                transaction.Start();
                try
                {
                    string target = Target(finding);
                    if (target.Length > 0)
                        ApplyPractice(doc, finding, target, outcome);
                    else if (finding.FixKind == DmFixKind.BindParameter)
                        Bind(doc, finding, stage, includeConditional, outcome);
                    else
                        SetParameter(doc, finding, outcome);

                    if (outcome.Changed > 0)
                        transaction.Commit();
                    else
                        transaction.RollBack();
                }
                catch (Exception ex)
                {
                    transaction.RollBack();
                    outcome.Changed = 0;
                    outcome.Message = "The fix failed and nothing was changed: " + ex.Message;
                    return outcome;
                }
            }

            if (outcome.Message.Length == 0)
                outcome.Message = outcome.Changed > 0
                    ? outcome.Summarize("Fix applied") + " Undo (Ctrl+Z) reverts it."
                    : outcome.Summarize("Nothing changed");
            return outcome;
        }

        internal static string Target(DmFinding finding)
        {
            return finding.FixData.TryGetValue("target", out string? value) ? value ?? "" : "";
        }

        // ── binding the DM shared parameters ────────────────────────────────────

        private static void Bind(Document doc, DmFinding finding, DmPermitStage stage, bool includeConditional,
                                 DmFixOutcome outcome)
        {
            // Only the attributes and categories of this finding, so "fix this issue" stays
            // narrower than the dashboard's "Bind DM parameters" button.
            Dictionary<string, List<BuiltInCategory>> all = DmSharedParameters.RequiredBindings(stage, includeConditional);
            var wanted = new Dictionary<string, List<BuiltInCategory>>(StringComparer.OrdinalIgnoreCase);

            var categories = new List<BuiltInCategory>();
            foreach (string name in finding.Categories)
            {
                if (Enum.TryParse(name, true, out BuiltInCategory category))
                    categories.Add(category);
            }

            foreach (string parameter in finding.ParametersToBind.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (categories.Count > 0)
                    wanted[parameter] = categories;
                else if (all.TryGetValue(parameter, out List<BuiltInCategory>? fromCatalog))
                    wanted[parameter] = fromCatalog;
                else
                    outcome.Skipped.Add(parameter + ": not a DM shared parameter");
            }

            if (wanted.Count == 0)
                return;

            DmBindResult bind = DmSharedParameters.Bind(doc, wanted);
            outcome.Changed = bind.Created + bind.Extended;
            foreach (string message in bind.Messages.Take(8))
                outcome.Skipped.Add(message);
            outcome.Message = bind.Summary +
                              (outcome.Changed > 0
                                  ? " The attributes exist now — run the audit again to fill the values."
                                  : " Nothing had to be created.");
        }

        // ── filling one attribute from the model ────────────────────────────────

        private static void SetParameter(Document doc, DmFinding finding, DmFixOutcome outcome)
        {
            string name = finding.ParameterName;
            foreach (long raw in finding.ElementIds)
            {
                Element? element = doc.GetElement(new ElementId(raw));
                if (element == null)
                    continue;

                Parameter? parameter = element.LookupParameter(name);
                if (parameter == null)
                {
                    Skip(outcome, raw, "the parameter is not bound to this element");
                    continue;
                }
                if (parameter.IsReadOnly)
                {
                    Skip(outcome, raw, "read-only (set it on the type instead)");
                    continue;
                }

                string value = Derive(doc, element, name);
                if (value.Length == 0)
                {
                    Skip(outcome, raw, "no value could be derived from the model");
                    continue;
                }

                if (Write(parameter, value, out string error))
                    outcome.Changed++;
                else
                    Skip(outcome, raw, error);
            }
        }

        /// <summary>Attributes whose value the model itself answers. Everything else is a decision.</summary>
        private static bool Derivable(string parameterName)
        {
            switch (parameterName)
            {
                case "IsExternal":
                case "LoadBearing":
                case "FireRating":
                case "IfcMaterial":
                case "Status":
                case "SpaceUsageDescription":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The value of a DM attribute as the model already states it, or "" when the model does
        /// not answer it. Mirrors the derivations the fix scripts carry, so the button and the
        /// script produce the same result.
        /// </summary>
        private static string Derive(Document doc, Element element, string parameterName)
        {
            switch (parameterName)
            {
                case "IsExternal":
                {
                    // Exterior, foundation and retaining walls are external; a door or window
                    // inherits the answer from the wall that hosts it.
                    Element host = element;
                    if (element is FamilyInstance instance && instance.Host != null)
                        host = instance.Host;
                    if (doc.GetElement(host.GetTypeId()) is WallType wallType)
                    {
                        Parameter? function = wallType.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
                        if (function != null && function.HasValue)
                            return function.AsInteger() == (int)WallFunction.Interior ? "0" : "1";
                    }
                    return host is RoofBase ? "1" : "";
                }

                case "LoadBearing":
                {
                    Parameter? structural = element.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
                    if (structural != null && structural.HasValue)
                        return structural.AsInteger() != 0 ? "1" : "0";
                    long categoryId = element.Category?.Id.Value ?? -1;
                    if (categoryId == (long)BuiltInCategory.OST_StructuralColumns ||
                        categoryId == (long)BuiltInCategory.OST_StructuralFraming ||
                        categoryId == (long)BuiltInCategory.OST_StructuralFoundation)
                        return "1";
                    return "0";
                }

                case "FireRating":
                {
                    Element? type = doc.GetElement(element.GetTypeId());
                    if (type == null)
                        return "";
                    Parameter? rating = type.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING) ??
                                        type.LookupParameter("Fire Rating");
                    if (rating == null || !rating.HasValue)
                        return "";
                    string text = rating.StorageType == StorageType.String
                        ? rating.AsString() ?? ""
                        : rating.AsValueString() ?? "";
                    return text.Trim();
                }

                case "IfcMaterial":
                {
                    Parameter? material = element.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                    if (material != null && material.HasValue &&
                        doc.GetElement(material.AsElementId()) is Material structural)
                        return structural.Name;
                    try
                    {
                        foreach (ElementId id in element.GetMaterialIds(false))
                        {
                            if (doc.GetElement(id) is Material first)
                                return first.Name;
                        }
                    }
                    catch
                    {
                        // elements without readable materials simply have no value here
                    }
                    return "";
                }

                case "Status":
                {
                    Parameter? phase = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                    string phaseName = phase != null && phase.HasValue ? phase.AsValueString() ?? "" : "";
                    return phaseName.IndexOf("EXIST", StringComparison.OrdinalIgnoreCase) >= 0 ? "Existing" : "New";
                }

                case "SpaceUsageDescription":
                {
                    Parameter? code = element.LookupParameter("SpaceUsageCode");
                    string codeValue = code != null && code.StorageType == StorageType.String
                        ? code.AsString() ?? ""
                        : "";
                    return codeValue.Length > 0 &&
                           DmKnowledgeBase.SpaceUsageCodes.TryGetValue(codeValue.Trim(), out DmUsageCode? usage)
                        ? usage.Description
                        : "";
                }

                default:
                    return "";
            }
        }

        // ── shared helpers ──────────────────────────────────────────────────────

        /// <summary>Writes a text value into whatever storage type the parameter uses.</summary>
        internal static bool Write(Parameter parameter, string value, out string error)
        {
            error = "";
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.Set(value);
                    case StorageType.Integer:
                    {
                        string upper = value.Trim().ToUpperInvariant();
                        if (upper == "1" || upper == "YES" || upper == "TRUE")
                            return parameter.Set(1);
                        if (upper == "0" || upper == "NO" || upper == "FALSE")
                            return parameter.Set(0);
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                            return parameter.Set(Convert.ToInt32(number));
                        error = "\"" + value + "\" is not a number";
                        return false;
                    }
                    case StorageType.Double:
                    {
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                            return parameter.Set(number);
                        error = "\"" + value + "\" is not a number";
                        return false;
                    }
                    default:
                        error = "unsupported storage type";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static void Skip(DmFixOutcome outcome, long id, string reason)
        {
            if (outcome.Skipped.Count < 40)
                outcome.Skipped.Add(id.ToString(CultureInfo.InvariantCulture) + ": " + reason);
        }

        internal static void Skip(DmFixOutcome outcome, string what, string reason)
        {
            if (outcome.Skipped.Count < 40)
                outcome.Skipped.Add(what + ": " + reason);
        }
    }
}
