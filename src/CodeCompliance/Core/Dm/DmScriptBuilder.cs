using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// Builds the C# that Claude sends to Revit with the revit-mcp tool
    /// <c>send_code_to_revit</c>, so a finding is fixed by running code against the open
    /// model instead of by hand.
    ///
    /// The host compiles the snippet into
    /// <c>public static object Execute(Document document, object[] parameters)</c> with
    /// System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB and
    /// Autodesk.Revit.UI in scope, using the CodeDom C# 5 compiler, and it wraps the call in
    /// its own transaction. Everything generated here therefore avoids string interpolation,
    /// pattern matching, tuples and local functions, never opens a transaction of its own,
    /// and returns a string summary.
    /// </summary>
    public static partial class DmScriptBuilder
    {
        private const int MaxIdsInScript = 3000;

        /// <summary>The script that fixes a finding, or "" when a script cannot fix it.</summary>
        public static string ForFinding(DmFinding finding)
        {
            // A modelling-practice finding is not a parameter value: it has its own script
            // (re-constrain, re-host, place rooms, map to IfcCovering …). When the practice
            // needs a person the script is empty and the generic fixes below take over.
            if (finding.PracticeId.Length > 0)
            {
                string practiceScript = ForPractice(finding);
                if (practiceScript.Length > 0)
                    return practiceScript;
                if (finding.FixKind == DmFixKind.ModelChange || finding.FixKind == DmFixKind.Review)
                    return "";
            }

            switch (finding.FixKind)
            {
                case DmFixKind.BindParameter:
                    return BindParameters(finding);
                case DmFixKind.SetParameter:
                    return SetParameter(finding);
                case DmFixKind.Rename:
                    return Rename(finding);
                default:
                    return "";
            }
        }

        // ── binding the DM shared parameters ────────────────────────────────────

        /// <summary>
        /// Creates the missing DM shared parameters from the file the plugin generates and
        /// binds them to the categories of the finding. Nothing has to be downloaded: the
        /// definitions come from the plugin's own copy of the DM attribute data set.
        /// </summary>
        public static string BindParameters(DmFinding finding)
        {
            List<string> names = finding.ParametersToBind.Count > 0
                ? finding.ParametersToBind
                : new List<string> { finding.ParameterName };
            names = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            if (names.Count == 0 || finding.Categories.Count == 0)
                return "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Creates the DM shared parameters and binds them to the categories that need them.");
            sb.AppendLine("// The parameter file was written by the plugin from its own copy of the Dubai");
            sb.AppendLine("// Municipality attribute data set - nothing has to be downloaded or uploaded.");
            sb.AppendLine();
            sb.AppendLine("var parameterFile = @\"" + DmSharedParameters.FilePath + "\";");
            sb.AppendLine("var names = new string[] {");
            sb.AppendLine("    " + string.Join(", ", names.Select(Quote)));
            sb.AppendLine("};");
            sb.AppendLine("var categories = new BuiltInCategory[] {");
            sb.AppendLine("    " + string.Join(", ", finding.Categories.Select(c => "BuiltInCategory." + c)));
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("var app = document.Application;");
            sb.AppendLine("var previousFile = app.SharedParametersFilename;");
            sb.AppendLine("app.SharedParametersFilename = parameterFile;");
            sb.AppendLine("var definitionFile = app.OpenSharedParameterFile();");
            sb.AppendLine("if (definitionFile == null) { return \"Could not open \" + parameterFile + \" - run the DM Compliance dashboard once so the plugin writes it.\"; }");
            sb.AppendLine("var group = definitionFile.Groups.get_Item(\"" + DmSharedParameters.GroupName + "\");");
            sb.AppendLine("if (group == null) { return \"The shared parameter file has no 'Building Permit' group.\"; }");
            sb.AppendLine();
            sb.AppendLine("var categorySet = app.Create.NewCategorySet();");
            sb.AppendLine("foreach (var builtIn in categories)");
            sb.AppendLine("{");
            sb.AppendLine("    var category = document.Settings.Categories.get_Item(builtIn);");
            sb.AppendLine("    if (category != null && category.AllowsBoundParameters) { categorySet.Insert(category); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("var bound = 0; var extended = 0; var skipped = new List<string>();");
            sb.AppendLine("foreach (var name in names)");
            sb.AppendLine("{");
            sb.AppendLine("    var definition = group.Definitions.get_Item(name) as ExternalDefinition;");
            sb.AppendLine("    if (definition == null) { skipped.Add(name + \": not in the DM parameter file\"); continue; }");
            sb.AppendLine("    var existing = document.ParameterBindings.get_Item(definition) as ElementBinding;");
            sb.AppendLine("    if (existing == null)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (document.ParameterBindings.Insert(definition, app.Create.NewInstanceBinding(categorySet), GroupTypeId.Data)) { bound++; }");
            sb.AppendLine("        else { skipped.Add(name + \": Revit refused the binding\"); }");
            sb.AppendLine("        continue;");
            sb.AppendLine("    }");
            sb.AppendLine("    var merged = app.Create.NewCategorySet();");
            sb.AppendLine("    foreach (Category category in existing.Categories) { merged.Insert(category); }");
            sb.AppendLine("    var added = false;");
            sb.AppendLine("    foreach (Category category in categorySet)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (!existing.Categories.Contains(category)) { merged.Insert(category); added = true; }");
            sb.AppendLine("    }");
            sb.AppendLine("    if (!added) { continue; }");
            sb.AppendLine("    if (document.ParameterBindings.ReInsert(definition, app.Create.NewInstanceBinding(merged), GroupTypeId.Data)) { extended++; }");
            sb.AppendLine("    else { skipped.Add(name + \": Revit refused the extended binding\"); }");
            sb.AppendLine("}");
            sb.AppendLine("app.SharedParametersFilename = previousFile;");
            sb.AppendLine();
            sb.AppendLine("return \"Bound \" + bound + \" new parameter(s), extended \" + extended + \", skipped \" + skipped.Count + (skipped.Count > 0 ? \": \" + string.Join(\"; \", skipped.ToArray()) : \"\");");
            return sb.ToString();
        }

        // ── filling a parameter value ───────────────────────────────────────────

        /// <summary>Fills one DM attribute on the elements of the finding.</summary>
        public static string SetParameter(DmFinding finding)
        {
            if (finding.ParameterName.Length == 0 || finding.ElementIds.Count == 0)
                return "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Fills \"" + finding.ParameterName + "\" on the elements the audit flagged.");
            string derivation = Derivation(finding.ParameterName);
            if (derivation.Length == 0)
            {
                sb.AppendLine("// Set the value below before running: the audit cannot derive it from the model.");
            }
            sb.AppendLine();
            sb.AppendLine("var parameterName = " + Quote(finding.ParameterName) + ";");
            sb.AppendLine("var fallbackValue = " + Quote(finding.SampleValue.Length > 0 ? finding.SampleValue : "") +
                          ";   // used when the value cannot be read from the model");
            string preamble = Preamble(finding.ParameterName);
            if (preamble.Length > 0)
            {
                sb.AppendLine();
                sb.Append(preamble);
            }
            sb.AppendLine();
            sb.AppendLine("var ids = new long[] {");
            AppendIds(sb, finding.ElementIds);
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("Func<Element, string> compute = delegate(Element element)");
            sb.AppendLine("{");
            if (derivation.Length > 0)
                sb.Append(derivation);
            else
                sb.AppendLine("    return fallbackValue;");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("var changed = 0; var skipped = new List<string>();");
            sb.AppendLine("foreach (var raw in ids)");
            sb.AppendLine("{");
            sb.AppendLine("    var element = document.GetElement(new ElementId(raw));");
            sb.AppendLine("    if (element == null) { continue; }");
            sb.AppendLine("    var parameter = element.LookupParameter(parameterName);");
            sb.AppendLine("    if (parameter == null) { skipped.Add(raw + \": parameter not bound\"); continue; }");
            sb.AppendLine("    if (parameter.IsReadOnly) { skipped.Add(raw + \": read-only (set it on the type instead)\"); continue; }");
            sb.AppendLine("    var value = compute(element);");
            sb.AppendLine("    if (value == null || value.Length == 0) { skipped.Add(raw + \": no value could be derived\"); continue; }");
            sb.AppendLine("    try");
            sb.AppendLine("    {");
            sb.AppendLine("        if (parameter.StorageType == StorageType.String) { parameter.Set(value); }");
            sb.AppendLine("        else if (parameter.StorageType == StorageType.Integer)");
            sb.AppendLine("        {");
            sb.AppendLine("            var yes = value == \"1\" || value.ToUpper() == \"YES\" || value.ToUpper() == \"TRUE\";");
            sb.AppendLine("            var no = value == \"0\" || value.ToUpper() == \"NO\" || value.ToUpper() == \"FALSE\";");
            sb.AppendLine("            if (yes) { parameter.Set(1); }");
            sb.AppendLine("            else if (no) { parameter.Set(0); }");
            sb.AppendLine("            else { parameter.Set(Convert.ToInt32(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture))); }");
            sb.AppendLine("        }");
            sb.AppendLine("        else if (parameter.StorageType == StorageType.Double)");
            sb.AppendLine("        {");
            sb.AppendLine("            parameter.Set(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));");
            sb.AppendLine("        }");
            sb.AppendLine("        else { skipped.Add(raw + \": unsupported storage type\"); continue; }");
            sb.AppendLine("        changed++;");
            sb.AppendLine("    }");
            sb.AppendLine("    catch (Exception ex) { skipped.Add(raw + \": \" + ex.Message); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Set \" + parameterName + \" on \" + changed + \" element(s); skipped \" + skipped.Count + (skipped.Count > 0 ? \": \" + string.Join(\"; \", skipped.Take(20).ToArray()) : \"\");");
            return sb.ToString();
        }

        /// <summary>
        /// How the value of a DM attribute is read out of the Revit model, as the body of the
        /// compute delegate. Empty when the value cannot be derived and has to be decided.
        /// </summary>
        private static string Derivation(string parameterName)
        {
            switch (parameterName)
            {
                case "IsExternal":
                    return
                        "    // Exterior, foundation and retaining walls are external; a door or window\n" +
                        "    // inherits the answer from the wall that hosts it.\n" +
                        "    var host = element;\n" +
                        "    var instance = element as FamilyInstance;\n" +
                        "    if (instance != null && instance.Host != null) { host = instance.Host; }\n" +
                        "    var hostType = document.GetElement(host.GetTypeId()) as WallType;\n" +
                        "    if (hostType != null)\n" +
                        "    {\n" +
                        "        var function = hostType.get_Parameter(BuiltInParameter.FUNCTION_PARAM);\n" +
                        "        if (function != null) { return function.AsInteger() == 0 ? \"0\" : \"1\"; }\n" +
                        "    }\n" +
                        "    var roof = host as RoofBase;\n" +
                        "    if (roof != null) { return \"1\"; }\n" +
                        "    return fallbackValue;\n";
                case "LoadBearing":
                    return
                        "    // Structural walls, columns, slabs and foundations are load bearing.\n" +
                        "    var structural = element.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);\n" +
                        "    if (structural != null) { return structural.AsInteger() != 0 ? \"1\" : \"0\"; }\n" +
                        "    var categoryId = element.Category != null ? element.Category.Id.Value : -1;\n" +
                        "    if (categoryId == (long)BuiltInCategory.OST_StructuralColumns ||\n" +
                        "        categoryId == (long)BuiltInCategory.OST_StructuralFraming ||\n" +
                        "        categoryId == (long)BuiltInCategory.OST_StructuralFoundation) { return \"1\"; }\n" +
                        "    return \"0\";\n";
                case "FireRating":
                    return
                        "    // Reuse the fire rating already carried by the element type.\n" +
                        "    var elementType = document.GetElement(element.GetTypeId());\n" +
                        "    if (elementType != null)\n" +
                        "    {\n" +
                        "        var rating = elementType.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING);\n" +
                        "        if (rating == null) { rating = elementType.LookupParameter(\"Fire Rating\"); }\n" +
                        "        if (rating != null && rating.HasValue)\n" +
                        "        {\n" +
                        "            var text = rating.StorageType == StorageType.String ? rating.AsString() : rating.AsValueString();\n" +
                        "            if (text != null && text.Length > 0) { return text; }\n" +
                        "        }\n" +
                        "    }\n" +
                        "    return fallbackValue;\n";
                case "IfcMaterial":
                    return
                        "    // Name the dominant material of the element.\n" +
                        "    var materialParameter = element.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);\n" +
                        "    if (materialParameter != null && materialParameter.HasValue)\n" +
                        "    {\n" +
                        "        var material = document.GetElement(materialParameter.AsElementId()) as Material;\n" +
                        "        if (material != null) { return material.Name; }\n" +
                        "    }\n" +
                        "    var materialIds = element.GetMaterialIds(false);\n" +
                        "    foreach (var materialId in materialIds)\n" +
                        "    {\n" +
                        "        var material = document.GetElement(materialId) as Material;\n" +
                        "        if (material != null) { return material.Name; }\n" +
                        "    }\n" +
                        "    return fallbackValue;\n";
                case "Status":
                    return
                        "    // New work unless the phase says otherwise.\n" +
                        "    var phase = element.get_Parameter(BuiltInParameter.PHASE_CREATED);\n" +
                        "    if (phase != null && phase.HasValue)\n" +
                        "    {\n" +
                        "        var phaseName = phase.AsValueString();\n" +
                        "        if (phaseName != null && phaseName.ToUpper().Contains(\"EXIST\")) { return \"Existing\"; }\n" +
                        "    }\n" +
                        "    return \"New\";\n";
                case "SpaceUsageDescription":
                    return
                        "    // The Appendix C description that belongs to the code on the same room.\n" +
                        "    var code = element.LookupParameter(\"SpaceUsageCode\");\n" +
                        "    if (code != null && code.StorageType == StorageType.String)\n" +
                        "    {\n" +
                        "        var codeValue = code.AsString();\n" +
                        "        if (codeValue != null && descriptions.ContainsKey(codeValue)) { return descriptions[codeValue]; }\n" +
                        "    }\n" +
                        "    return fallbackValue;\n";
                default:
                    return "";
            }
        }

        /// <summary>Extra declarations a derivation needs before the loop.</summary>
        public static string Preamble(string parameterName)
        {
            if (parameterName != "SpaceUsageDescription")
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("var descriptions = new Dictionary<string, string>();");
            foreach (KeyValuePair<string, DmUsageCode> code in DmKnowledgeBase.SpaceUsageCodes
                         .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine("descriptions[" + Quote(code.Key) + "] = " + Quote(code.Value.Description) + ";");
            }
            return sb.ToString();
        }

        // ── renaming ────────────────────────────────────────────────────────────

        /// <summary>Renames the elements of a finding from a mapping you confirm first.</summary>
        public static string Rename(DmFinding finding)
        {
            if (finding.ElementIds.Count == 0)
                return "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Renames the flagged elements. Fill in the target names first: the audit knows");
            sb.AppendLine("// the rule, not the name you want for each element.");
            sb.AppendLine("// DM level names:  B1_BASEMENT1, GA_GATE LEVEL, GR_GROUND FLOOR, P1_PODIUM1,");
            sb.AppendLine("//                  M1_MEZZANINE1, F1_FLOOR1, S1_SERVICE1, RF_ROOF");
            sb.AppendLine("// DM room numbers: <LEVEL>-<3 digits>, e.g. F1-001");
            sb.AppendLine("// DM type names:   CATEGORY_FUNCTIONALTYPE_DISCIPLINE_DESCRIPTION, max 30 characters,");
            sb.AppendLine("//                  uppercase abbreviations, underscores instead of spaces");
            sb.AppendLine();
            sb.AppendLine("// Current names of the flagged elements (read them first, then fill newNames):");
            sb.AppendLine("var ids = new long[] {");
            AppendIds(sb, finding.ElementIds);
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("var newNames = new Dictionary<long, string>();");
            sb.AppendLine("// newNames[123456] = \"F1_FLOOR1\";");
            sb.AppendLine();
            sb.AppendLine("if (newNames.Count == 0)");
            sb.AppendLine("{");
            sb.AppendLine("    var listing = new List<string>();");
            sb.AppendLine("    foreach (var raw in ids)");
            sb.AppendLine("    {");
            sb.AppendLine("        var element = document.GetElement(new ElementId(raw));");
            sb.AppendLine("        if (element != null) { listing.Add(raw + \" = \" + element.Name); }");
            sb.AppendLine("    }");
            sb.AppendLine("    return \"Current names (nothing changed yet):\\n\" + string.Join(\"\\n\", listing.ToArray());");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("var renamed = 0; var failed = new List<string>();");
            sb.AppendLine("foreach (var entry in newNames)");
            sb.AppendLine("{");
            sb.AppendLine("    var element = document.GetElement(new ElementId(entry.Key));");
            sb.AppendLine("    if (element == null) { failed.Add(entry.Key + \": not found\"); continue; }");
            sb.AppendLine("    try { element.Name = entry.Value; renamed++; }");
            sb.AppendLine("    catch (Exception ex) { failed.Add(entry.Key + \": \" + ex.Message); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Renamed \" + renamed + \" element(s); \" + failed.Count + \" failed\" + (failed.Count > 0 ? \": \" + string.Join(\"; \", failed.ToArray()) : \"\");");
            return sb.ToString();
        }

        // ── helpers ─────────────────────────────────────────────────────────────

        private static void Header(StringBuilder sb, DmFinding finding)
        {
            sb.AppendLine("// DM BIM compliance fix — " + finding.Scope + ": " + finding.Title);
            sb.AppendLine("// Reference: " + finding.Reference);
            sb.AppendLine("// Run with the revit-mcp tool send_code_to_revit. The host wraps this in its own");
            sb.AppendLine("// transaction, so do not start one here.");
            sb.AppendLine();
        }

        private static void AppendIds(StringBuilder sb, IList<long> ids)
        {
            int count = Math.Min(ids.Count, MaxIdsInScript);
            for (int i = 0; i < count; i += 12)
            {
                IEnumerable<long> line = ids.Skip(i).Take(Math.Min(12, count - i));
                sb.AppendLine("    " + string.Join(", ", line.Select(id => id.ToString(CultureInfo.InvariantCulture))) +
                              (i + 12 < count ? "," : ""));
            }
            if (ids.Count > MaxIdsInScript)
            {
                sb.AppendLine("    // " + (ids.Count - MaxIdsInScript) + " further ids are in the CSV report; run the");
                sb.AppendLine("    // script again with them, or collect the elements with a FilteredElementCollector.");
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", " ").Replace("\n", " ") + "\"";
        }
    }
}
