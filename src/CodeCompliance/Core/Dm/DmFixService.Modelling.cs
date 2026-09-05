using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// The direct fixes for phase 7 — DM's recommended modelling practices. Same changes the
    /// generated scripts describe, made natively so they work without the MCP link and without
    /// a C# compiler at run time.
    ///
    /// Practices that need a decision (splitting columns, remodelling an object, resolving a
    /// clash with a link, deleting a redundant room, purging) are deliberately not fixable here.
    /// </summary>
    public static partial class DmFixService
    {
        /// <summary>Offset parameters, tried in order, when an element is moved to another level.</summary>
        private static readonly BuiltInParameter[] OffsetParameters =
        {
            BuiltInParameter.WALL_BASE_OFFSET,
            BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM,
            BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM,
            BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM,
            BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,
            BuiltInParameter.INSTANCE_ELEVATION_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM,
            BuiltInParameter.STAIRS_BASE_OFFSET,
            BuiltInParameter.ROOM_LOWER_OFFSET
        };

        /// <summary>Level parameters, tried in order, when an element is moved to another level.</summary>
        private static readonly BuiltInParameter[] LevelParameters =
        {
            BuiltInParameter.WALL_BASE_CONSTRAINT,
            BuiltInParameter.LEVEL_PARAM,
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,
            BuiltInParameter.FAMILY_LEVEL_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
            BuiltInParameter.ROOF_BASE_LEVEL_PARAM,
            BuiltInParameter.STAIRS_BASE_LEVEL_PARAM,
            BuiltInParameter.ROOM_LEVEL_ID
        };

        private static bool CanFixPractice(string target)
        {
            switch (target)
            {
                case "base":
                case "top":
                case "top-constraint":
                case "room-bounding":
                case "place-rooms":
                case "rehost":
                case "rehost-nearest":
                case "ifc-class":
                case "dont-export":
                case "space-height":
                case "dummy-level":
                    return true;
                default:
                    return false;   // list-rooms, purge and everything else need a person
            }
        }

        private static string WhyNotPractice(string target)
        {
            switch (target)
            {
                case "list-rooms":
                    return "Rooms sharing a region are never deleted automatically — which of the two to keep is " +
                           "your call. The prompt lists them with number, name, level and area.";
                case "purge":
                    return "Purging removes content from the project, so it is left to Manage ▸ Purge Unused. " +
                           "The prompt lists what would go.";
                default:
                    return "This practice needs a modelling decision — splitting an element, remodelling it or " +
                           "resolving a clash. The prompt explains what DM asks for.";
            }
        }

        private static string DescribePractice(DmFinding finding, string target)
        {
            int count = finding.ElementIds.Count;
            switch (target)
            {
                case "base":
                    return "Set the base offset of " + count + " wall(s) to " +
                           finding.FixData.TryGet("defaultOffsetMillimetres", "-100") +
                           " mm so they reach down to SSL.";
                case "top":
                    return "Stop " + count + " wall(s) under the slab above, using the slab thickness of their top " +
                           "level. Walls whose top level carries no floor are left alone.";
                case "top-constraint":
                    return "Constrain " + count + " wall(s) to the level above and stop them under its slab.";
                case "room-bounding":
                    return "Switch \"Room Bounding\" on for " + count + " wall(s) / column(s).";
                case "place-rooms":
                    return "Place rooms automatically in every unfilled enclosed region of " + count +
                           " level(s). Only rooms are added — nothing existing is touched — and the new rooms " +
                           "still need their name, number and usage code.";
                case "rehost":
                    return "Move " + count + " element(s) onto \"" + finding.FixData.TryGet("levelName", "the target level") +
                           "\", compensating the offset so the geometry does not move.";
                case "rehost-nearest":
                    return "Move " + finding.ElementFixData.Count + " element(s) onto the level their geometry " +
                           "actually sits on, compensating the offset so nothing moves.";
                case "ifc-class":
                    return "Set IfcExportAs = " + finding.FixData.TryGet("ifcClass", "IfcCovering") +
                           " (and the predefined type " + finding.FixData.TryGet("predefinedType", "") +
                           ") on the types of " + count + " element(s).";
                case "dont-export":
                    return "Set IfcExportAs = DontExport on the types of " + count + " element(s), so they stay " +
                           "in the model but never reach the IFC. Nothing is deleted.";
                case "space-height":
                    return "Raise " + finding.ElementFixData.Count + " room(s) to the ceiling height the audit " +
                           "read from their ceilings.";
                case "dummy-level":
                    return "Create a dummy level (Building Story cleared) per elevated group and move " + count +
                           " element(s) onto it, keeping the geometry where it is.";
                default:
                    return "";
            }
        }

        private static void ApplyPractice(Document doc, DmFinding finding, string target, DmFixOutcome outcome)
        {
            switch (target)
            {
                case "base": WallBaseOffset(doc, finding, outcome); break;
                case "top": WallTopOffset(doc, finding, outcome, false); break;
                case "top-constraint": WallTopOffset(doc, finding, outcome, true); break;
                case "room-bounding": RoomBounding(doc, finding, outcome); break;
                case "place-rooms": PlaceRooms(doc, finding, outcome); break;
                case "rehost": Rehost(doc, finding, outcome); break;
                case "rehost-nearest": RehostNearest(doc, finding, outcome); break;
                case "ifc-class":
                case "dont-export": IfcClass(doc, finding, outcome); break;
                case "space-height": SpaceHeight(doc, finding, outcome); break;
                case "dummy-level": DummyLevel(doc, finding, outcome); break;
            }
        }

        // ── RMP-01 · wall constraints ───────────────────────────────────────────

        private static void WallBaseOffset(Document doc, DmFinding finding, DmFixOutcome outcome)
        {
            double millimetres = -100;
            double.TryParse(finding.FixData.TryGet("defaultOffsetMillimetres", "-100"),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out millimetres);
            double offset = UnitUtils.ConvertToInternalUnits(millimetres / 1000.0, UnitTypeId.Meters);

            foreach (long raw in finding.ElementIds)
            {
                if (!(doc.GetElement(new ElementId(raw)) is Wall wall))
                    continue;
                Parameter? parameter = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                if (parameter == null || parameter.IsReadOnly)
                {
                    Skip(outcome, raw, "base offset is read-only");
                    continue;
                }
                try
                {
                    parameter.Set(offset);
                    outcome.Changed++;
                }
                catch (Exception ex)
                {
                    Skip(outcome, raw, ex.Message);
                }
            }

            outcome.Message = outcome.Summarize("Wall base offset set to " +
                                                millimetres.ToString("F0", CultureInfo.InvariantCulture) + " mm");
        }

        private static void WallTopOffset(Document doc, DmFinding finding, DmFixOutcome outcome, bool setConstraint)
        {
            Dictionary<long, double> thickness = SlabThicknessByLevel(doc);
            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            foreach (long raw in finding.ElementIds)
            {
                if (!(doc.GetElement(new ElementId(raw)) is Wall wall))
                    continue;

                Parameter? topConstraint = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                Parameter? topOffset = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                if (topConstraint == null || topOffset == null)
                {
                    Skip(outcome, raw, "not a level-constrained wall");
                    continue;
                }

                if (setConstraint && topConstraint.AsElementId() == ElementId.InvalidElementId)
                {
                    if (!(doc.GetElement(wall.LevelId) is Level baseLevel))
                    {
                        Skip(outcome, raw, "no base level");
                        continue;
                    }
                    Level? above = levels.FirstOrDefault(l => l.Elevation > baseLevel.Elevation + 1e-3);
                    if (above == null)
                    {
                        Skip(outcome, raw, "no level above \"" + baseLevel.Name + "\"");
                        continue;
                    }
                    if (topConstraint.IsReadOnly)
                    {
                        Skip(outcome, raw, "top constraint is read-only");
                        continue;
                    }
                    topConstraint.Set(above.Id);
                }

                ElementId topLevelId = topConstraint.AsElementId();
                if (topLevelId == ElementId.InvalidElementId)
                {
                    Skip(outcome, raw, "still an unconnected height");
                    continue;
                }
                if (!thickness.TryGetValue(topLevelId.Value, out double slab) || slab <= 0)
                {
                    Skip(outcome, raw, "the top level carries no floor, so the slab thickness is unknown");
                    continue;
                }
                if (topOffset.IsReadOnly)
                {
                    Skip(outcome, raw, "top offset is read-only");
                    continue;
                }
                try
                {
                    topOffset.Set(-slab);
                    outcome.Changed++;
                }
                catch (Exception ex)
                {
                    Skip(outcome, raw, ex.Message);
                }
            }

            outcome.Message = outcome.Summarize("Walls stopped under the slab above");
        }

        /// <summary>Thickest floor found on each level — the slab a wall below has to stop under.</summary>
        private static Dictionary<long, double> SlabThicknessByLevel(Document doc)
        {
            var map = new Dictionary<long, double>();
            foreach (Element floor in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Floors)
                         .WhereElementIsNotElementType())
            {
                if (floor.LevelId == ElementId.InvalidElementId)
                    continue;
                Element? type = doc.GetElement(floor.GetTypeId());
                if (type == null)
                    continue;
                Parameter? parameter = type.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM) ??
                                       type.LookupParameter("Default Thickness");
                if (parameter == null || parameter.StorageType != StorageType.Double)
                    continue;
                double value = parameter.AsDouble();
                long key = floor.LevelId.Value;
                if (!map.TryGetValue(key, out double current) || current < value)
                    map[key] = value;
            }
            return map;
        }

        // ── RMP-03 · room bounding ──────────────────────────────────────────────

        private static void RoomBounding(Document doc, DmFinding finding, DmFixOutcome outcome)
        {
            var doneTypes = new HashSet<long>();
            int onTypes = 0;

            foreach (long raw in finding.ElementIds)
            {
                Element? element = doc.GetElement(new ElementId(raw));
                if (element == null)
                    continue;

                Parameter? parameter = element.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING) ??
                                       element.LookupParameter("Room Bounding");
                if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Integer)
                {
                    try
                    {
                        parameter.Set(1);
                        outcome.Changed++;
                    }
                    catch (Exception ex)
                    {
                        Skip(outcome, raw, ex.Message);
                    }
                    continue;
                }

                // Column families carry the flag on the type.
                ElementId typeId = element.GetTypeId();
                if (typeId == ElementId.InvalidElementId || !doneTypes.Add(typeId.Value))
                    continue;
                Parameter? onType = doc.GetElement(typeId)?.LookupParameter("Room Bounding");
                if (onType == null || onType.IsReadOnly || onType.StorageType != StorageType.Integer)
                {
                    Skip(outcome, raw, "no writable Room Bounding parameter (tick it in the family instead)");
                    continue;
                }
                try
                {
                    onType.Set(1);
                    onTypes++;
                    outcome.Changed++;
                }
                catch (Exception ex)
                {
                    Skip(outcome, raw, ex.Message);
                }
            }

            outcome.Message = outcome.Summarize("Room bounding switched on" +
                                                (onTypes > 0 ? " (" + onTypes + " on the family type)" : ""));
        }

        // ── RMP-04 · place the missing rooms ────────────────────────────────────

        private static void PlaceRooms(Document doc, DmFinding finding, DmFixOutcome outcome)
        {
            var report = new List<string>();
            foreach (long raw in finding.ElementIds)
            {
                if (!(doc.GetElement(new ElementId(raw)) is Level level))
                    continue;
                try
                {
                    ICollection<ElementId>? created = doc.Create.NewRooms2(level);
                    int count = created?.Count ?? 0;
                    outcome.Changed += count;
                    report.Add(level.Name + ": " + count);
                }
                catch (Exception ex)
                {
                    Skip(outcome, level.Name, ex.Message);
                }
            }

            outcome.Message = "Placed " + outcome.Changed + " room(s)" +
                              (report.Count > 0 ? " — " + string.Join(", ", report) : "") +
                              ". Name, number and the DM usage codes still have to be filled in.";
        }

        // ── RMP-05 / RMP-06 · re-host on the right level ────────────────────────

        private static void Rehost(Document doc, DmFinding finding, DmFixOutcome outcome)
        {
            if (!long.TryParse(finding.FixData.TryGet("levelId", ""), NumberStyles.Integer,
                               CultureInfo.InvariantCulture, out long levelId))
                return;
            if (!(doc.GetElement(new ElementId(levelId)) is Level target))
            {
                outcome.Message = "The target level no longer exists — run the audit again.";
                return;
            }

            foreach (long raw in finding.ElementIds)
            {
                Element? element = doc.GetElement(new ElementId(raw));
                if (element == null)
                    continue;
                double delta = 0.0;
                if (doc.GetElement(element.LevelId) is Level current)
                    delta = current.Elevation - target.Elevation;
                Move(doc, element, target.Id, delta, outcome, raw);
            }

            outcome.Message = outcome.Summarize("Re-hosted onto \"" + target.Name + "\"");
        }

        private static void RehostNearest(Document doc, DmFinding finding, DmFixOutcome outcome)
        {
            foreach (KeyValuePair<long, string> entry in finding.ElementFixData)
            {
                Element? element = doc.GetElement(new ElementId(entry.Key));
                if (element == null)
                    continue;

                string[] parts = entry.Value.Split('|');
                if (parts.Length < 2 ||
                    !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long levelId) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double delta))
                {
                    Skip(outcome, entry.Key, "the target level of this element is unknown");
                    continue;
                }
                if (!(doc.GetElement(new ElementId(levelId)) is Level))
                {
                    Skip(outcome, entry.Key, "the target level no longer exists");
                    continue;
                }
                Move(doc, element, new ElementId(levelId), delta, outcome, entry.Key);
            }

            outcome.Message = outcome.Summarize("Re-hosted onto the level the geometry sits on");
        }

        /// <summary>
        /// Moves an element to another level and compensates its offset by the elevation
        /// difference, so the geometry stays exactly where it is.
        /// </summary>
        private static void Move(Document doc, Element element, ElementId levelId, double delta,
                                 DmFixOutcome outcome, long raw)
        {
            Parameter? levelParameter = First(element, LevelParameters, StorageType.ElementId);
            if (levelParameter == null)
            {
                Skip(outcome, raw, "the level of this element cannot be changed (re-create it on the right level)");
                return;
            }
            Parameter? offsetParameter = First(element, OffsetParameters, StorageType.Double);

            try
            {
                double oldOffset = offsetParameter?.AsDouble() ?? 0.0;
                levelParameter.Set(levelId);
                if (offsetParameter != null)
                    offsetParameter.Set(oldOffset + delta);
                else if (Math.Abs(delta) > 1e-3)
                    Skip(outcome, raw, "level changed but the offset could not be compensated — check its position");
                outcome.Changed++;
            }
            catch (Exception ex)
            {
                Skip(outcome, raw, ex.Message);
            }
            _ = doc;
        }

        private static Parameter? First(Element element, BuiltInParameter[] candidates, StorageType storage)
        {
            foreach (BuiltInParameter builtIn in candidates)
            {
                Parameter? parameter = element.get_Parameter(builtIn);
                if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == storage)
                    return parameter;
            }
            return null;
        }

        // ── RMP-09 / RMP-13 · the IFC class of the element type ─────────────────

        private static void IfcClass(Document doc, DmFinding finding, DmFixOutcome outcome)
        {
            string ifcClass = finding.FixData.TryGet("ifcClass",
                finding.SampleValue.Length > 0 ? finding.SampleValue : "DontExport");
            string predefined = finding.FixData.TryGet("predefinedType", "");
            var done = new HashSet<long>();

            foreach (long raw in finding.ElementIds)
            {
                Element? element = doc.GetElement(new ElementId(raw));
                if (element == null)
                    continue;
                ElementId typeId = element.GetTypeId();
                if (typeId == ElementId.InvalidElementId || !done.Add(typeId.Value))
                    continue;
                Element? type = doc.GetElement(typeId);
                if (type == null)
                    continue;

                Parameter? exportAs = type.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS) ??
                                      type.LookupParameter("IfcExportAs");
                if (exportAs == null || exportAs.IsReadOnly || exportAs.StorageType != StorageType.String)
                {
                    Skip(outcome, SafeTypeName(type),
                         "no writable IfcExportAs — map the type in the DM category mapping file at export instead");
                    continue;
                }

                try
                {
                    exportAs.Set(ifcClass);
                    if (predefined.Length > 0)
                    {
                        Parameter? exportType = type.LookupParameter("IfcExportType") ??
                                                type.LookupParameter("Type IFC Predefined Type");
                        if (exportType != null && !exportType.IsReadOnly &&
                            exportType.StorageType == StorageType.String)
                            exportType.Set(predefined);
                    }
                    outcome.Changed++;
                }
                catch (Exception ex)
                {
                    Skip(outcome, SafeTypeName(type), ex.Message);
                }
            }

            outcome.Message = outcome.Summarize("IfcExportAs = " + ifcClass + " written on the element types");
        }

        private static string SafeTypeName(Element type)
        {
            try
            {
                return type.Name ?? "type";
            }
            catch
            {
                return "type";
            }
        }

        // ── RMP-10 · space height up to the ceiling ─────────────────────────────

        private static void SpaceHeight(Document doc, DmFinding finding, DmFixOutcome outcome)
        {
            foreach (KeyValuePair<long, string> entry in finding.ElementFixData)
            {
                Element? room = doc.GetElement(new ElementId(entry.Key));
                if (room == null)
                    continue;
                if (!double.TryParse(entry.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double height) ||
                    height <= 0)
                {
                    Skip(outcome, entry.Key, "no ceiling height was measured for this room");
                    continue;
                }

                Parameter? upperLevel = room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL);
                Parameter? upperOffset = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
                if (upperOffset == null || upperOffset.IsReadOnly)
                {
                    Skip(outcome, entry.Key, "limit offset is read-only");
                    continue;
                }
                try
                {
                    // The upper limit becomes the room's own level, so the limit offset is the
                    // clear height itself rather than a difference between two levels.
                    if (upperLevel != null && !upperLevel.IsReadOnly && room.LevelId != ElementId.InvalidElementId)
                        upperLevel.Set(room.LevelId);
                    upperOffset.Set(height);
                    outcome.Changed++;
                }
                catch (Exception ex)
                {
                    Skip(outcome, entry.Key, ex.Message);
                }
            }

            outcome.Message = outcome.Summarize("Room heights raised to their ceiling");
        }

        // ── RMP-14 · dummy level for the elevated part of a storey ──────────────

        private static void DummyLevel(Document doc, DmFinding finding, DmFixOutcome outcome)
        {
            // Group the elements by the elevation they actually sit at.
            var byElevation = new Dictionary<long, List<Element>>();
            var elevationOf = new Dictionary<long, double>();

            foreach (long raw in finding.ElementIds)
            {
                Element? element = doc.GetElement(new ElementId(raw));
                if (element == null)
                    continue;
                if (!(doc.GetElement(element.LevelId) is Level level))
                    continue;
                Parameter? offsetParameter = First(element, OffsetParameters, StorageType.Double);
                double elevation = level.Elevation + (offsetParameter?.AsDouble() ?? 0.0);
                long key = (long)Math.Round(elevation * 1000.0);
                if (!byElevation.TryGetValue(key, out List<Element>? list))
                {
                    list = new List<Element>();
                    byElevation[key] = list;
                }
                list.Add(element);
                elevationOf[key] = elevation;
            }

            List<Level> existing = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();

            int created = 0;
            var report = new List<string>();
            int index = 1;

            foreach (KeyValuePair<long, List<Element>> group in byElevation)
            {
                double elevation = elevationOf[group.Key];
                Level? dummy = existing.FirstOrDefault(l => Math.Abs(l.Elevation - elevation) < 0.003);
                if (dummy == null)
                {
                    try
                    {
                        dummy = Level.Create(doc, elevation);
                        try
                        {
                            dummy.Name = "DL" + index + "_DUMMY LEVEL " + index;
                        }
                        catch
                        {
                            // a name clash keeps Revit's generated name
                        }
                        existing.Add(dummy);
                        created++;
                    }
                    catch (Exception ex)
                    {
                        Skip(outcome, "level at " + elevation.ToString("F3", CultureInfo.InvariantCulture),
                             ex.Message);
                        continue;
                    }
                }
                index++;

                Parameter? story = dummy.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
                if (story != null && !story.IsReadOnly)
                    story.Set(0);

                foreach (Element element in group.Value)
                {
                    Parameter? levelParameter = First(element, LevelParameters, StorageType.ElementId);
                    if (levelParameter == null)
                    {
                        Skip(outcome, element.Id.Value, "the level of this element cannot be changed");
                        continue;
                    }
                    Parameter? offsetParameter = First(element, OffsetParameters, StorageType.Double);
                    try
                    {
                        levelParameter.Set(dummy.Id);
                        if (offsetParameter != null)
                            offsetParameter.Set(0.0);
                        outcome.Changed++;
                    }
                    catch (Exception ex)
                    {
                        Skip(outcome, element.Id.Value, ex.Message);
                    }
                }

                report.Add(dummy.Name + ": " + group.Value.Count);
            }

            outcome.Message = "Created " + created + " dummy level(s) and moved " + outcome.Changed +
                              " element(s)" + (report.Count > 0 ? " — " + string.Join(", ", report) : "") +
                              ". Rename them to the project convention and check the IFC afterwards." +
                              (outcome.Skipped.Count > 0 ? " Skipped " + outcome.Skipped.Count + "." : "");
        }
    }

    /// <summary>Small helper so the fix code reads the audit's fix data without noise.</summary>
    internal static class DmFixDataExtensions
    {
        public static string TryGet(this Dictionary<string, string> data, string key, string fallback)
        {
            return data.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value) ? value : fallback;
        }
    }
}
