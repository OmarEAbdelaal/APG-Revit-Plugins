using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// The fix scripts of phase 7 — DM's recommended modelling practices. A modelling practice
    /// is not a parameter value, so each one gets its own script: re-constrain the walls,
    /// re-host the elements on the level they sit on, place the missing rooms, raise the space
    /// height to the ceiling, map the finishes to IfcCovering, keep the unwanted content out of
    /// the IFC.
    ///
    /// Everything generated here obeys the same host contract as the rest of the builder: C# 5
    /// (no interpolation, no null-conditional operators, no pattern matching), no transaction
    /// of its own, <c>document</c> in scope, and a string summary as the return value. Nothing
    /// is deleted and no geometry is moved without the change being reported back.
    /// </summary>
    public static partial class DmScriptBuilder
    {
        /// <summary>The script for a modelling-practice finding, or "" when it needs a person.</summary>
        public static string ForPractice(DmFinding finding)
        {
            string target = finding.FixData.TryGetValue("target", out string? value) ? value : "";
            switch (target)
            {
                case "base": return WallBaseOffset(finding);
                case "top": return WallTopOffset(finding, false);
                case "top-constraint": return WallTopOffset(finding, true);
                case "room-bounding": return RoomBounding(finding);
                case "place-rooms": return PlaceRooms(finding);
                case "rehost": return Rehost(finding);
                case "rehost-nearest": return RehostNearest(finding);
                case "ifc-class": return IfcClass(finding);
                case "dont-export": return IfcClass(finding);
                case "space-height": return SpaceHeight(finding);
                case "list-rooms": return ListRooms(finding);
                case "dummy-level": return DummyLevel(finding);
                case "purge": return ListUnusedTypes(finding);
                default: return "";
            }
        }

        // ── RMP-01 · wall constraints ───────────────────────────────────────────

        private static string WallBaseOffset(DmFinding finding)
        {
            if (finding.ElementIds.Count == 0)
                return "";

            double millimetres = -100;
            if (finding.FixData.TryGetValue("defaultOffsetMillimetres", out string? raw))
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out millimetres);

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Takes the base of the flagged walls down to the structural slab level (SSL).");
            sb.AppendLine("// The offset is the thickness of the floor finish of that storey: set it to the");
            sb.AppendLine("// value of this project before running, the default below is only a placeholder.");
            sb.AppendLine();
            sb.AppendLine("var baseOffsetMillimetres = " + millimetres.ToString("F0", CultureInfo.InvariantCulture) + ";");
            sb.AppendLine("var baseOffset = UnitUtils.ConvertToInternalUnits(baseOffsetMillimetres / 1000.0, UnitTypeId.Meters);");
            sb.AppendLine();
            AppendIdArray(sb, finding.ElementIds);
            sb.AppendLine("var changed = 0; var skipped = new List<string>();");
            sb.AppendLine("foreach (var raw in ids)");
            sb.AppendLine("{");
            sb.AppendLine("    var wall = document.GetElement(new ElementId(raw)) as Wall;");
            sb.AppendLine("    if (wall == null) { continue; }");
            sb.AppendLine("    var parameter = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);");
            sb.AppendLine("    if (parameter == null || parameter.IsReadOnly) { skipped.Add(raw + \": base offset is read-only\"); continue; }");
            sb.AppendLine("    try { parameter.Set(baseOffset); changed++; }");
            sb.AppendLine("    catch (Exception ex) { skipped.Add(raw + \": \" + ex.Message); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Set the base offset of \" + changed + \" wall(s) to \" + baseOffsetMillimetres + \" mm; skipped \" + skipped.Count + (skipped.Count > 0 ? \": \" + string.Join(\"; \", skipped.Take(20).ToArray()) : \"\");");
            return sb.ToString();
        }

        /// <summary>
        /// Stops the walls under the slab above: the top offset becomes minus the thickness of
        /// the floor found on the top constraint level. When <paramref name="setConstraint"/>
        /// is true the top constraint itself is set to the next level up first (walls modelled
        /// with an unconnected height).
        /// </summary>
        private static string WallTopOffset(DmFinding finding, bool setConstraint)
        {
            if (finding.ElementIds.Count == 0)
                return "";

            var sb = new StringBuilder();
            Header(sb, finding);
            if (setConstraint)
            {
                sb.AppendLine("// Constrains the walls to the level above and stops them at the underside of its slab.");
            }
            else
            {
                sb.AppendLine("// Stops the walls at the underside of the slab of their top constraint level.");
            }
            sb.AppendLine("// The slab thickness is read from the floors of that level; walls whose top level");
            sb.AppendLine("// carries no floor are reported instead of being guessed.");
            sb.AppendLine();
            sb.AppendLine("// Slab thickness per level, from the floors placed on it.");
            sb.AppendLine("var thicknessByLevel = new Dictionary<long, double>();");
            sb.AppendLine("var floors = new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType().ToElements();");
            sb.AppendLine("foreach (var floor in floors)");
            sb.AppendLine("{");
            sb.AppendLine("    if (floor.LevelId == ElementId.InvalidElementId) { continue; }");
            sb.AppendLine("    var floorType = document.GetElement(floor.GetTypeId());");
            sb.AppendLine("    if (floorType == null) { continue; }");
            sb.AppendLine("    var thicknessParameter = floorType.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);");
            sb.AppendLine("    if (thicknessParameter == null) { thicknessParameter = floorType.LookupParameter(\"Default Thickness\"); }");
            sb.AppendLine("    if (thicknessParameter == null || thicknessParameter.StorageType != StorageType.Double) { continue; }");
            sb.AppendLine("    var thickness = thicknessParameter.AsDouble();");
            sb.AppendLine("    var key = floor.LevelId.Value;");
            sb.AppendLine("    if (!thicknessByLevel.ContainsKey(key) || thicknessByLevel[key] < thickness) { thicknessByLevel[key] = thickness; }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();");
            sb.AppendLine();
            AppendIdArray(sb, finding.ElementIds);
            sb.AppendLine("var changed = 0; var skipped = new List<string>();");
            sb.AppendLine("foreach (var raw in ids)");
            sb.AppendLine("{");
            sb.AppendLine("    var wall = document.GetElement(new ElementId(raw)) as Wall;");
            sb.AppendLine("    if (wall == null) { continue; }");
            sb.AppendLine("    var topParameter = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);");
            sb.AppendLine("    var offsetParameter = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);");
            sb.AppendLine("    if (topParameter == null || offsetParameter == null) { skipped.Add(raw + \": not a level-constrained wall\"); continue; }");
            if (setConstraint)
            {
                sb.AppendLine("    if (topParameter.AsElementId() == ElementId.InvalidElementId)");
                sb.AppendLine("    {");
                sb.AppendLine("        var baseLevel = document.GetElement(wall.LevelId) as Level;");
                sb.AppendLine("        if (baseLevel == null) { skipped.Add(raw + \": no base level\"); continue; }");
                sb.AppendLine("        Level above = null;");
                sb.AppendLine("        foreach (var level in levels) { if (level.Elevation > baseLevel.Elevation + 0.001) { above = level; break; } }");
                sb.AppendLine("        if (above == null) { skipped.Add(raw + \": no level above \" + baseLevel.Name); continue; }");
                sb.AppendLine("        if (topParameter.IsReadOnly) { skipped.Add(raw + \": top constraint is read-only\"); continue; }");
                sb.AppendLine("        topParameter.Set(above.Id);");
                sb.AppendLine("    }");
            }
            sb.AppendLine("    var topLevelId = topParameter.AsElementId();");
            sb.AppendLine("    if (topLevelId == ElementId.InvalidElementId) { skipped.Add(raw + \": still unconnected\"); continue; }");
            sb.AppendLine("    if (!thicknessByLevel.ContainsKey(topLevelId.Value)) { skipped.Add(raw + \": no floor on the top level, thickness unknown\"); continue; }");
            sb.AppendLine("    if (offsetParameter.IsReadOnly) { skipped.Add(raw + \": top offset is read-only\"); continue; }");
            sb.AppendLine("    try { offsetParameter.Set(-thicknessByLevel[topLevelId.Value]); changed++; }");
            sb.AppendLine("    catch (Exception ex) { skipped.Add(raw + \": \" + ex.Message); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Stopped \" + changed + \" wall(s) under the slab above; skipped \" + skipped.Count + (skipped.Count > 0 ? \": \" + string.Join(\"; \", skipped.Take(20).ToArray()) : \"\");");
            return sb.ToString();
        }

        // ── RMP-03 · room bounding ──────────────────────────────────────────────

        private static string RoomBounding(DmFinding finding)
        {
            if (finding.ElementIds.Count == 0)
                return "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Switches \"Room Bounding\" on for the flagged walls and columns, so the room");
            sb.AppendLine("// boundaries stop at their faces. Nothing else about the elements changes.");
            sb.AppendLine();
            AppendIdArray(sb, finding.ElementIds);
            sb.AppendLine("var changed = 0; var onTypes = 0; var skipped = new List<string>();");
            sb.AppendLine("var doneTypes = new HashSet<long>();");
            sb.AppendLine("foreach (var raw in ids)");
            sb.AppendLine("{");
            sb.AppendLine("    var element = document.GetElement(new ElementId(raw));");
            sb.AppendLine("    if (element == null) { continue; }");
            sb.AppendLine("    var parameter = element.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);");
            sb.AppendLine("    if (parameter == null) { parameter = element.LookupParameter(\"Room Bounding\"); }");
            sb.AppendLine("    if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Integer)");
            sb.AppendLine("    {");
            sb.AppendLine("        try { parameter.Set(1); changed++; continue; }");
            sb.AppendLine("        catch (Exception ex) { skipped.Add(raw + \": \" + ex.Message); continue; }");
            sb.AppendLine("    }");
            sb.AppendLine("    // Column families carry the flag on the type.");
            sb.AppendLine("    var typeId = element.GetTypeId();");
            sb.AppendLine("    if (typeId == ElementId.InvalidElementId || doneTypes.Contains(typeId.Value)) { continue; }");
            sb.AppendLine("    var elementType = document.GetElement(typeId);");
            sb.AppendLine("    var typeParameter = elementType == null ? null : elementType.LookupParameter(\"Room Bounding\");");
            sb.AppendLine("    if (typeParameter == null || typeParameter.IsReadOnly || typeParameter.StorageType != StorageType.Integer)");
            sb.AppendLine("    { skipped.Add(raw + \": no writable Room Bounding parameter (edit the family and tick it there)\"); continue; }");
            sb.AppendLine("    try { typeParameter.Set(1); doneTypes.Add(typeId.Value); onTypes++; }");
            sb.AppendLine("    catch (Exception ex) { skipped.Add(raw + \": \" + ex.Message); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Room bounding set on \" + changed + \" element(s) and \" + onTypes + \" type(s); skipped \" + skipped.Count + (skipped.Count > 0 ? \": \" + string.Join(\"; \", skipped.Take(20).ToArray()) : \"\");");
            return sb.ToString();
        }

        // ── RMP-04 · place the missing rooms ────────────────────────────────────

        private static string PlaceRooms(DmFinding finding)
        {
            if (finding.ElementIds.Count == 0)
                return "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Places a room in every enclosed region of the flagged levels that has none.");
            sb.AppendLine("// It only adds rooms - no existing room, wall or boundary is touched. The new");
            sb.AppendLine("// rooms still need their name, number and DM usage code afterwards.");
            sb.AppendLine();
            AppendIdArray(sb, finding.ElementIds, "levelIds");
            sb.AppendLine("var placed = 0; var report = new List<string>();");
            sb.AppendLine("foreach (var raw in levelIds)");
            sb.AppendLine("{");
            sb.AppendLine("    var level = document.GetElement(new ElementId(raw)) as Level;");
            sb.AppendLine("    if (level == null) { continue; }");
            sb.AppendLine("    try");
            sb.AppendLine("    {");
            sb.AppendLine("        var created = document.Create.NewRooms2(level);");
            sb.AppendLine("        var count = created == null ? 0 : created.Count;");
            sb.AppendLine("        placed += count;");
            sb.AppendLine("        report.Add(level.Name + \": \" + count + \" new room(s)\");");
            sb.AppendLine("    }");
            sb.AppendLine("    catch (Exception ex) { report.Add(level.Name + \": \" + ex.Message); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Placed \" + placed + \" room(s). \" + string.Join(\"; \", report.ToArray()) + \" - name, number and usage code still have to be filled in.\";");
            return sb.ToString();
        }

        // ── RMP-05 / RMP-06 · re-host on the right level ────────────────────────

        private static string Rehost(DmFinding finding)
        {
            if (finding.ElementIds.Count == 0 || !finding.FixData.TryGetValue("levelId", out string? levelId))
                return "";

            string levelName = finding.FixData.TryGetValue("levelName", out string? name) ? name : "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Moves the flagged elements onto \"" + levelName + "\" and compensates their offset");
            sb.AppendLine("// by the elevation difference, so the geometry stays exactly where it is.");
            sb.AppendLine();
            sb.AppendLine("var targetLevelId = new ElementId(" + levelId + "L);");
            sb.AppendLine();
            AppendIdArray(sb, finding.ElementIds);
            AppendRehostBody(sb, false);
            return sb.ToString();
        }

        private static string RehostNearest(DmFinding finding)
        {
            if (finding.ElementFixData.Count == 0)
                return "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Moves every flagged element onto the level its geometry actually sits on, and");
            sb.AppendLine("// compensates the offset by the elevation difference so nothing moves in space.");
            sb.AppendLine("// The target level per element was computed by the audit from the element geometry.");
            sb.AppendLine();
            sb.AppendLine("var targets = new Dictionary<long, long>();     // element id -> level id");
            sb.AppendLine("var deltas = new Dictionary<long, double>();    // element id -> elevation difference (feet)");
            foreach (KeyValuePair<long, string> entry in finding.ElementFixData.OrderBy(e => e.Key))
            {
                string[] parts = entry.Value.Split('|');
                if (parts.Length < 2)
                    continue;
                sb.AppendLine("targets[" + entry.Key.ToString(CultureInfo.InvariantCulture) + "L] = " +
                              parts[0] + "L;   // " + (parts.Length > 2 ? parts[2] : ""));
                sb.AppendLine("deltas[" + entry.Key.ToString(CultureInfo.InvariantCulture) + "L] = " +
                              Literal(parts[1]) + ";");
            }
            sb.AppendLine();
            sb.AppendLine("var ids = targets.Keys.ToList();");
            sb.AppendLine();
            AppendRehostBody(sb, true);
            return sb.ToString();
        }

        /// <summary>The shared body of both re-hosting scripts.</summary>
        private static void AppendRehostBody(StringBuilder sb, bool perElementTarget)
        {
            sb.AppendLine("// Offset parameters, in the order they are tried per element.");
            sb.AppendLine("var offsetParameters = new BuiltInParameter[] {");
            sb.AppendLine("    BuiltInParameter.WALL_BASE_OFFSET,");
            sb.AppendLine("    BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM,");
            sb.AppendLine("    BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,");
            sb.AppendLine("    BuiltInParameter.INSTANCE_ELEVATION_PARAM,");
            sb.AppendLine("    BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM,");
            sb.AppendLine("    BuiltInParameter.STAIRS_BASE_OFFSET,");
            sb.AppendLine("    BuiltInParameter.ROOM_LOWER_OFFSET");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("var levelParameters = new BuiltInParameter[] {");
            sb.AppendLine("    BuiltInParameter.WALL_BASE_CONSTRAINT,");
            sb.AppendLine("    BuiltInParameter.LEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.SCHEDULE_LEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.FAMILY_LEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.ROOF_BASE_LEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.STAIRS_BASE_LEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.ROOM_LEVEL_ID");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("var changed = 0; var skipped = new List<string>();");
            sb.AppendLine("foreach (var raw in ids)");
            sb.AppendLine("{");
            sb.AppendLine("    var element = document.GetElement(new ElementId(raw));");
            sb.AppendLine("    if (element == null) { continue; }");
            if (perElementTarget)
            {
                sb.AppendLine("    var targetLevelId = new ElementId(targets[raw]);");
                sb.AppendLine("    var delta = deltas[raw];");
            }
            else
            {
                sb.AppendLine("    var oldLevel = document.GetElement(element.LevelId) as Level;");
                sb.AppendLine("    var newLevel = document.GetElement(targetLevelId) as Level;");
                sb.AppendLine("    var delta = (oldLevel == null || newLevel == null) ? 0.0 : oldLevel.Elevation - newLevel.Elevation;");
            }
            sb.AppendLine();
            sb.AppendLine("    Parameter levelParameter = null;");
            sb.AppendLine("    foreach (var builtIn in levelParameters)");
            sb.AppendLine("    {");
            sb.AppendLine("        var candidate = element.get_Parameter(builtIn);");
            sb.AppendLine("        if (candidate != null && !candidate.IsReadOnly && candidate.StorageType == StorageType.ElementId)");
            sb.AppendLine("        { levelParameter = candidate; break; }");
            sb.AppendLine("    }");
            sb.AppendLine("    if (levelParameter == null) { skipped.Add(raw + \": the level of this element cannot be changed (re-create it on the right level)\"); continue; }");
            sb.AppendLine();
            sb.AppendLine("    Parameter offsetParameter = null;");
            sb.AppendLine("    foreach (var builtIn in offsetParameters)");
            sb.AppendLine("    {");
            sb.AppendLine("        var candidate = element.get_Parameter(builtIn);");
            sb.AppendLine("        if (candidate != null && !candidate.IsReadOnly && candidate.StorageType == StorageType.Double)");
            sb.AppendLine("        { offsetParameter = candidate; break; }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    try");
            sb.AppendLine("    {");
            sb.AppendLine("        var oldOffset = offsetParameter == null ? 0.0 : offsetParameter.AsDouble();");
            sb.AppendLine("        levelParameter.Set(targetLevelId);");
            sb.AppendLine("        if (offsetParameter != null) { offsetParameter.Set(oldOffset + delta); }");
            sb.AppendLine("        else if (Math.Abs(delta) > 0.001) { skipped.Add(raw + \": level changed but the offset could not be compensated - check its position\"); }");
            sb.AppendLine("        changed++;");
            sb.AppendLine("    }");
            sb.AppendLine("    catch (Exception ex) { skipped.Add(raw + \": \" + ex.Message); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Re-hosted \" + changed + \" element(s); skipped \" + skipped.Count + (skipped.Count > 0 ? \": \" + string.Join(\"; \", skipped.Take(20).ToArray()) : \"\");");
        }

        // ── RMP-09 / RMP-13 · the IFC class of the element type ─────────────────

        private static string IfcClass(DmFinding finding)
        {
            if (finding.ElementIds.Count == 0)
                return "";

            string ifcClass = finding.FixData.TryGetValue("ifcClass", out string? value)
                ? value
                : (finding.SampleValue.Length > 0 ? finding.SampleValue : "DontExport");
            string predefined = finding.FixData.TryGetValue("predefinedType", out string? predefinedType)
                ? predefinedType
                : "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Writes the IFC class on the TYPES of the flagged elements - that is where the");
            sb.AppendLine("// exporter reads it. One write per type, however many instances there are.");
            sb.AppendLine();
            sb.AppendLine("var ifcClass = " + Quote(ifcClass) + ";");
            sb.AppendLine("var predefinedType = " + Quote(predefined) + ";");
            sb.AppendLine();
            AppendIdArray(sb, finding.ElementIds);
            sb.AppendLine("var changed = 0; var skipped = new List<string>(); var done = new HashSet<long>();");
            sb.AppendLine("foreach (var raw in ids)");
            sb.AppendLine("{");
            sb.AppendLine("    var element = document.GetElement(new ElementId(raw));");
            sb.AppendLine("    if (element == null) { continue; }");
            sb.AppendLine("    var typeId = element.GetTypeId();");
            sb.AppendLine("    if (typeId == ElementId.InvalidElementId || !done.Add(typeId.Value)) { continue; }");
            sb.AppendLine("    var elementType = document.GetElement(typeId);");
            sb.AppendLine("    if (elementType == null) { continue; }");
            sb.AppendLine();
            sb.AppendLine("    var exportAs = elementType.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS);");
            sb.AppendLine("    if (exportAs == null) { exportAs = elementType.LookupParameter(\"IfcExportAs\"); }");
            sb.AppendLine("    if (exportAs == null || exportAs.IsReadOnly || exportAs.StorageType != StorageType.String)");
            sb.AppendLine("    { skipped.Add(elementType.Name + \": no writable IfcExportAs (add the IFC parameters, or map the type in the DM category mapping file)\"); continue; }");
            sb.AppendLine("    try");
            sb.AppendLine("    {");
            sb.AppendLine("        exportAs.Set(ifcClass);");
            sb.AppendLine("        if (predefinedType.Length > 0)");
            sb.AppendLine("        {");
            sb.AppendLine("            var exportType = elementType.LookupParameter(\"IfcExportType\");");
            sb.AppendLine("            if (exportType == null) { exportType = elementType.LookupParameter(\"Type IFC Predefined Type\"); }");
            sb.AppendLine("            if (exportType != null && !exportType.IsReadOnly && exportType.StorageType == StorageType.String)");
            sb.AppendLine("            { exportType.Set(predefinedType); }");
            sb.AppendLine("        }");
            sb.AppendLine("        changed++;");
            sb.AppendLine("    }");
            sb.AppendLine("    catch (Exception ex) { skipped.Add(elementType.Name + \": \" + ex.Message); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Set IfcExportAs = \" + ifcClass + \" on \" + changed + \" type(s); skipped \" + skipped.Count + (skipped.Count > 0 ? \": \" + string.Join(\"; \", skipped.Take(20).ToArray()) : \"\");");
            return sb.ToString();
        }

        // ── RMP-10 · space height up to the ceiling ─────────────────────────────

        private static string SpaceHeight(DmFinding finding)
        {
            if (finding.ElementFixData.Count == 0)
                return "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Raises (or lowers) each room to the ceiling of that room. The target height was");
            sb.AppendLine("// read by the audit from the ceilings inside the room; where a room has no ceiling");
            sb.AppendLine("// the storey height is used. The upper limit is set to the room's own level so the");
            sb.AppendLine("// limit offset is the clear height itself.");
            sb.AppendLine();
            sb.AppendLine("var heights = new Dictionary<long, double>();   // room id -> height above its level (feet)");
            foreach (KeyValuePair<long, string> entry in finding.ElementFixData.OrderBy(e => e.Key))
            {
                sb.AppendLine("heights[" + entry.Key.ToString(CultureInfo.InvariantCulture) + "L] = " +
                              Literal(entry.Value) + ";");
            }
            sb.AppendLine();
            sb.AppendLine("var changed = 0; var skipped = new List<string>();");
            sb.AppendLine("foreach (var entry in heights)");
            sb.AppendLine("{");
            sb.AppendLine("    var room = document.GetElement(new ElementId(entry.Key));");
            sb.AppendLine("    if (room == null) { continue; }");
            sb.AppendLine("    var upperLevel = room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL);");
            sb.AppendLine("    var upperOffset = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);");
            sb.AppendLine("    if (upperOffset == null || upperOffset.IsReadOnly) { skipped.Add(entry.Key + \": limit offset is read-only\"); continue; }");
            sb.AppendLine("    try");
            sb.AppendLine("    {");
            sb.AppendLine("        if (upperLevel != null && !upperLevel.IsReadOnly && room.LevelId != ElementId.InvalidElementId)");
            sb.AppendLine("        { upperLevel.Set(room.LevelId); }");
            sb.AppendLine("        upperOffset.Set(entry.Value);");
            sb.AppendLine("        changed++;");
            sb.AppendLine("    }");
            sb.AppendLine("    catch (Exception ex) { skipped.Add(entry.Key + \": \" + ex.Message); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Adjusted the height of \" + changed + \" room(s); skipped \" + skipped.Count + (skipped.Count > 0 ? \": \" + string.Join(\"; \", skipped.Take(20).ToArray()) : \"\");");
            return sb.ToString();
        }

        // ── RMP-12 · rooms sharing an enclosed region ───────────────────────────

        private static string ListRooms(DmFinding finding)
        {
            if (finding.ElementIds.Count == 0)
                return "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// READ-ONLY. Lists the rooms Revit reports in the same enclosed region with their");
            sb.AppendLine("// number, name, level and area, so it can be decided which one is kept. Nothing is");
            sb.AppendLine("// deleted here: fill deleteIds only after the decision and run the script again.");
            sb.AppendLine();
            AppendIdArray(sb, finding.ElementIds);
            sb.AppendLine("var deleteIds = new long[] { };   // e.g. { 123456, 123457 } - only after confirmation");
            sb.AppendLine();
            sb.AppendLine("if (deleteIds.Length == 0)");
            sb.AppendLine("{");
            sb.AppendLine("    var listing = new List<string>();");
            sb.AppendLine("    foreach (var raw in ids)");
            sb.AppendLine("    {");
            sb.AppendLine("        var room = document.GetElement(new ElementId(raw)) as SpatialElement;");
            sb.AppendLine("        if (room == null) { continue; }");
            sb.AppendLine("        var numberParameter = room.get_Parameter(BuiltInParameter.ROOM_NUMBER);");
            sb.AppendLine("        var nameParameter = room.get_Parameter(BuiltInParameter.ROOM_NAME);");
            sb.AppendLine("        var areaParameter = room.get_Parameter(BuiltInParameter.ROOM_AREA);");
            sb.AppendLine("        var level = document.GetElement(room.LevelId);");
            sb.AppendLine("        listing.Add(raw + \" | \" + (numberParameter == null ? \"\" : numberParameter.AsString()) +");
            sb.AppendLine("                    \" | \" + (nameParameter == null ? \"\" : nameParameter.AsString()) +");
            sb.AppendLine("                    \" | \" + (level == null ? \"\" : level.Name) +");
            sb.AppendLine("                    \" | \" + (areaParameter == null ? \"\" : areaParameter.AsValueString()));");
            sb.AppendLine("    }");
            sb.AppendLine("    return \"Rooms in a shared or open region (id | number | name | level | area) - nothing changed:\\n\" + string.Join(\"\\n\", listing.ToArray());");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("var deleted = 0; var failed = new List<string>();");
            sb.AppendLine("foreach (var raw in deleteIds)");
            sb.AppendLine("{");
            sb.AppendLine("    try { document.Delete(new ElementId(raw)); deleted++; }");
            sb.AppendLine("    catch (Exception ex) { failed.Add(raw + \": \" + ex.Message); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Deleted \" + deleted + \" redundant room(s); \" + failed.Count + \" failed\" + (failed.Count > 0 ? \": \" + string.Join(\"; \", failed.ToArray()) : \"\");");
            return sb.ToString();
        }

        // ── RMP-14 · dummy level for the elevated part of a storey ──────────────

        private static string DummyLevel(DmFinding finding)
        {
            if (finding.ElementIds.Count == 0)
                return "";

            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// Creates one dummy level per elevated group of elements, with \"Building Story\"");
            sb.AppendLine("// cleared so it never becomes an IfcBuildingStorey, and moves the elements onto it");
            sb.AppendLine("// with a zero offset. The geometry stays exactly where it is.");
            sb.AppendLine();
            AppendIdArray(sb, finding.ElementIds);
            sb.AppendLine("var offsetParameters = new BuiltInParameter[] {");
            sb.AppendLine("    BuiltInParameter.WALL_BASE_OFFSET,");
            sb.AppendLine("    BuiltInParameter.ROOM_LOWER_OFFSET,");
            sb.AppendLine("    BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,");
            sb.AppendLine("    BuiltInParameter.INSTANCE_ELEVATION_PARAM");
            sb.AppendLine("};");
            sb.AppendLine("var levelParameters = new BuiltInParameter[] {");
            sb.AppendLine("    BuiltInParameter.WALL_BASE_CONSTRAINT,");
            sb.AppendLine("    BuiltInParameter.ROOM_LEVEL_ID,");
            sb.AppendLine("    BuiltInParameter.LEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.SCHEDULE_LEVEL_PARAM,");
            sb.AppendLine("    BuiltInParameter.FAMILY_LEVEL_PARAM");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("// Group the elements by the elevation they actually sit at.");
            sb.AppendLine("var byElevation = new Dictionary<long, List<long>>();");
            sb.AppendLine("var elevationOf = new Dictionary<long, double>();");
            sb.AppendLine("foreach (var raw in ids)");
            sb.AppendLine("{");
            sb.AppendLine("    var element = document.GetElement(new ElementId(raw));");
            sb.AppendLine("    if (element == null) { continue; }");
            sb.AppendLine("    var level = document.GetElement(element.LevelId) as Level;");
            sb.AppendLine("    if (level == null) { continue; }");
            sb.AppendLine("    double offset = 0.0;");
            sb.AppendLine("    foreach (var builtIn in offsetParameters)");
            sb.AppendLine("    {");
            sb.AppendLine("        var candidate = element.get_Parameter(builtIn);");
            sb.AppendLine("        if (candidate != null && candidate.StorageType == StorageType.Double) { offset = candidate.AsDouble(); break; }");
            sb.AppendLine("    }");
            sb.AppendLine("    var elevation = level.Elevation + offset;");
            sb.AppendLine("    var key = (long)Math.Round(elevation * 1000.0);   // 1 mm buckets in feet*1000");
            sb.AppendLine("    if (!byElevation.ContainsKey(key)) { byElevation[key] = new List<long>(); }");
            sb.AppendLine("    byElevation[key].Add(raw);");
            sb.AppendLine("    elevationOf[key] = elevation;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("var created = 0; var moved = 0; var skipped = new List<string>(); var report = new List<string>();");
            sb.AppendLine("var index = 1;");
            sb.AppendLine("foreach (var group in byElevation)");
            sb.AppendLine("{");
            sb.AppendLine("    var elevation = elevationOf[group.Key];");
            sb.AppendLine("    Level dummy = null;");
            sb.AppendLine("    foreach (var level in new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>())");
            sb.AppendLine("    {");
            sb.AppendLine("        if (Math.Abs(level.Elevation - elevation) < 0.003) { dummy = level; break; }");
            sb.AppendLine("    }");
            sb.AppendLine("    if (dummy == null)");
            sb.AppendLine("    {");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            dummy = Level.Create(document, elevation);");
            sb.AppendLine("            dummy.Name = \"DL\" + index + \"_DUMMY LEVEL \" + index;");
            sb.AppendLine("            created++;");
            sb.AppendLine("        }");
            sb.AppendLine("        catch (Exception ex) { skipped.Add(\"level at \" + elevation + \": \" + ex.Message); continue; }");
            sb.AppendLine("    }");
            sb.AppendLine("    index++;");
            sb.AppendLine();
            sb.AppendLine("    var story = dummy.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);");
            sb.AppendLine("    if (story != null && !story.IsReadOnly) { story.Set(0); }");
            sb.AppendLine();
            sb.AppendLine("    foreach (var raw in group.Value)");
            sb.AppendLine("    {");
            sb.AppendLine("        var element = document.GetElement(new ElementId(raw));");
            sb.AppendLine("        if (element == null) { continue; }");
            sb.AppendLine("        Parameter levelParameter = null;");
            sb.AppendLine("        foreach (var builtIn in levelParameters)");
            sb.AppendLine("        {");
            sb.AppendLine("            var candidate = element.get_Parameter(builtIn);");
            sb.AppendLine("            if (candidate != null && !candidate.IsReadOnly && candidate.StorageType == StorageType.ElementId)");
            sb.AppendLine("            { levelParameter = candidate; break; }");
            sb.AppendLine("        }");
            sb.AppendLine("        if (levelParameter == null) { skipped.Add(raw + \": the level of this element cannot be changed\"); continue; }");
            sb.AppendLine("        Parameter offsetParameter = null;");
            sb.AppendLine("        foreach (var builtIn in offsetParameters)");
            sb.AppendLine("        {");
            sb.AppendLine("            var candidate = element.get_Parameter(builtIn);");
            sb.AppendLine("            if (candidate != null && !candidate.IsReadOnly && candidate.StorageType == StorageType.Double)");
            sb.AppendLine("            { offsetParameter = candidate; break; }");
            sb.AppendLine("        }");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            levelParameter.Set(dummy.Id);");
            sb.AppendLine("            if (offsetParameter != null) { offsetParameter.Set(0.0); }");
            sb.AppendLine("            moved++;");
            sb.AppendLine("        }");
            sb.AppendLine("        catch (Exception ex) { skipped.Add(raw + \": \" + ex.Message); }");
            sb.AppendLine("    }");
            sb.AppendLine("    report.Add(dummy.Name + \": \" + group.Value.Count + \" element(s)\");");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("return \"Created \" + created + \" dummy level(s), moved \" + moved + \" element(s) - \" + string.Join(\"; \", report.ToArray()) + \"; skipped \" + skipped.Count + (skipped.Count > 0 ? \": \" + string.Join(\"; \", skipped.Take(20).ToArray()) : \"\") + \". Rename the dummy levels to the project convention and check the IFC afterwards.\";");
            return sb.ToString();
        }

        // ── RMP-15 · what a purge would remove ──────────────────────────────────

        private static string ListUnusedTypes(DmFinding finding)
        {
            var sb = new StringBuilder();
            Header(sb, finding);
            sb.AppendLine("// READ-ONLY. Lists the family types that no instance uses, so it can be confirmed");
            sb.AppendLine("// what Purge Unused would remove. Deleting them is a separate, confirmed step.");
            sb.AppendLine();
            sb.AppendLine("var used = new HashSet<long>();");
            sb.AppendLine("foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType().ToElements())");
            sb.AppendLine("{");
            sb.AppendLine("    var typeId = element.GetTypeId();");
            sb.AppendLine("    if (typeId != ElementId.InvalidElementId) { used.Add(typeId.Value); }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("var unused = new List<string>();");
            sb.AppendLine("foreach (var type in new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())");
            sb.AppendLine("{");
            sb.AppendLine("    if (type.Category == null || type.Category.CategoryType != CategoryType.Model) { continue; }");
            sb.AppendLine("    if (used.Contains(type.Id.Value)) { continue; }");
            sb.AppendLine("    unused.Add(type.Category.Name + \" · \" + type.FamilyName + \" : \" + type.Name);");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("unused.Sort();");
            sb.AppendLine("return unused.Count + \" unused family type(s) - run Manage ▸ Purge Unused in Revit to remove them:\\n\" + string.Join(\"\\n\", unused.Take(200).ToArray());");
            return sb.ToString();
        }

        // ── helpers ─────────────────────────────────────────────────────────────

        private static void AppendIdArray(StringBuilder sb, IList<long> ids, string name = "ids")
        {
            sb.AppendLine("var " + name + " = new long[] {");
            AppendIds(sb, ids);
            sb.AppendLine("};");
            sb.AppendLine();
        }

        /// <summary>A double the generated C# can parse back, whatever the machine's locale.</summary>
        private static string Literal(string invariantNumber)
        {
            double value;
            if (!double.TryParse(invariantNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return "0.0";
            string text = value.ToString("R", CultureInfo.InvariantCulture);
            return text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0 ? text + ".0" : text;
        }
    }
}
