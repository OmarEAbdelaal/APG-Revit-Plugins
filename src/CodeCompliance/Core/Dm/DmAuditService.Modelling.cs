using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// Phase 7 of the audit: Dubai Municipality's <b>Recommended Modelling Practices</b> — how
    /// the model itself has to be built so the exported IFC survives DM's platform. The
    /// practices themselves (wording, severity, type of modification, tolerances) are data in
    /// <c>modelling_practices.json</c>; only the detection lives here, keyed on the practice id.
    ///
    /// Like the rest of the audit this is strictly read-only.
    /// </summary>
    public static partial class DmAuditService
    {
        private static void CheckModellingPractices(Document doc, DmAuditResult result,
                                                    DmParameters parameters, DmAuditOptions options)
        {
            int before = result.Findings.Count;
            int ran = 0;

            List<Level> storeys = BuildingStoreys(doc);

            foreach (DmModellingPractice practice in DmKnowledgeBase.ModellingPractices)
            {
                if (!practice.Enabled)
                    continue;
                ran++;
                try
                {
                    switch (practice.Id.ToUpperInvariant())
                    {
                        case "RMP-01": CheckWallConstraints(doc, result, options, practice, storeys); break;
                        case "RMP-02": CheckColumnStoreys(doc, result, options, practice, storeys); break;
                        case "RMP-03": CheckRoomBounding(doc, result, options, practice); break;
                        case "RMP-04": CheckSpaceCoverage(doc, result, options, practice, storeys); break;
                        case "RMP-05": CheckSiteLevels(doc, result, options, practice); break;
                        case "RMP-06": CheckLevelAssociation(doc, result, options, practice, storeys); break;
                        case "RMP-07": CheckFinishedAndStructuralLevels(doc, result, options, practice); break;
                        case "RMP-08": CheckModellingTools(doc, result, options, practice); break;
                        case "RMP-09": CheckFinishIfcClass(doc, result, options, practice); break;
                        case "RMP-10": CheckSpaceHeight(doc, result, options, practice, storeys); break;
                        case "RMP-11": CheckLinkClashes(doc, result, options, practice); break;
                        case "RMP-12": CheckOneRoomPerRegion(doc, result, options, practice); break;
                        case "RMP-13": CheckUnwantedElements(doc, result, options, practice); break;
                        case "RMP-14": CheckSplitLevels(doc, result, options, practice, storeys); break;
                        case "RMP-15": CheckExportPreparation(doc, result, options, practice); break;
                        case "RMP-16": CheckWallStoreys(doc, result, options, practice, storeys); break;
                    }
                }
                catch
                {
                    // One practice that cannot be evaluated on this model (a category missing in
                    // this Revit version, unreadable geometry) must never stop the audit.
                }
            }

            Summarize(result, DmCheckGroup.ModellingPractices,
                      "DM recommended modelling practices (" + ran + " practices)", ran, before);
        }

        // ── RMP-01 · wall base and top constraints ──────────────────────────────

        private static void CheckWallConstraints(Document doc, DmAuditResult result, DmAuditOptions options,
                                                 DmModellingPractice practice, List<Level> storeys)
        {
            double maxBase = practice.Metres("maximumBaseOffsetMillimetres", 0);
            double maxTop = practice.Metres("maximumTopOffsetMillimetres", 0);
            bool flagUnconnected = practice.Flag("flagUnconnectedHeight", true);
            bool ignoreCurtain = practice.Flag("ignoreCurtainWalls", true);
            double tolerance = MetersToFeet(0.005);

            var positiveBase = new List<Element>();
            var positiveTop = new List<Element>();
            var unconnected = new List<Element>();
            int checkedWalls = 0;

            foreach (Wall wall in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Walls)
                         .WhereElementIsNotElementType()
                         .OfClass(typeof(Wall))
                         .Cast<Wall>())
            {
                if (ignoreCurtain && IsCurtainWall(wall))
                    continue;
                if (wall.LevelId == ElementId.InvalidElementId)
                    continue;
                checkedWalls++;

                double baseOffset = Value(wall, BuiltInParameter.WALL_BASE_OFFSET);
                double topOffset = Value(wall, BuiltInParameter.WALL_TOP_OFFSET);
                ElementId topConstraint = ElementIdValue(wall, BuiltInParameter.WALL_HEIGHT_TYPE);

                // The base may sit exactly on the level (that only misses the floor finish);
                // anything above it is reported. The top has to stay strictly below the level
                // above, so a top offset of zero already means the wall runs into that slab.
                if (baseOffset > MetersToFeet(maxBase) + tolerance)
                    positiveBase.Add(wall);

                if (topConstraint == ElementId.InvalidElementId)
                {
                    if (flagUnconnected)
                        unconnected.Add(wall);
                }
                else if (topOffset >= MetersToFeet(maxTop) - tolerance)
                {
                    positiveTop.Add(wall);
                }
            }

            if (checkedWalls == 0)
                return;

            if (positiveBase.Count > 0)
            {
                DmFinding finding = PracticeFinding(practice,
                    positiveBase.Count + " of " + checkedWalls + " wall(s) start above their level instead of at SSL",
                    "These walls carry a base offset of zero or more, so they sit on top of the floor finish " +
                    "instead of running down to the structural slab level.");
                finding.CheckedCount = checkedWalls;
                finding.AffectedCount = positiveBase.Count;
                finding.FixData["target"] = "base";
                finding.FixData["defaultOffsetMillimetres"] =
                    practice.Number("defaultBaseOffsetMillimetres", -100).ToString("F0", CultureInfo.InvariantCulture);
                finding.Categories.Add(BuiltInCategory.OST_Walls.ToString());
                Add(result, finding, result.ModelTitle, positiveBase, options);
            }

            if (positiveTop.Count > 0)
            {
                DmFinding finding = PracticeFinding(practice,
                    positiveTop.Count + " of " + checkedWalls + " wall(s) clash into the slab above",
                    "The top offset of these walls is zero or positive against their top constraint, so the wall " +
                    "runs into the slab of the level above instead of stopping at its underside.");
                finding.CheckedCount = checkedWalls;
                finding.AffectedCount = positiveTop.Count;
                finding.FixData["target"] = "top";
                finding.Categories.Add(BuiltInCategory.OST_Walls.ToString());
                Add(result, finding, result.ModelTitle, positiveTop, options);
            }

            if (unconnected.Count > 0)
            {
                DmFinding finding = PracticeFinding(practice,
                    unconnected.Count + " of " + checkedWalls + " wall(s) use an unconnected height",
                    "A wall without a top constraint keeps its height when the storey height changes and cannot " +
                    "be related to the level above, so it ends up crossing or missing the slab.");
                finding.Severity = DmSeverity.Warning;
                finding.CheckedCount = checkedWalls;
                finding.AffectedCount = unconnected.Count;
                finding.FixData["target"] = "top-constraint";
                finding.Categories.Add(BuiltInCategory.OST_Walls.ToString());
                Add(result, finding, result.ModelTitle, unconnected, options);
            }

            _ = storeys;
        }

        private static bool IsCurtainWall(Wall wall)
        {
            try
            {
                return wall.CurtainGrid != null || wall.WallType?.Kind == WallKind.Curtain;
            }
            catch
            {
                return false;
            }
        }

        // ── RMP-02 · one column per storey ──────────────────────────────────────

        private static void CheckColumnStoreys(Document doc, DmAuditResult result, DmAuditOptions options,
                                               DmModellingPractice practice, List<Level> storeys)
        {
            int maxSpan = (int)practice.Number("maximumStoreysSpanned", 1);
            var spanning = new List<Element>();
            var details = new List<string>();
            int checkedColumns = 0;

            foreach (Element column in Collect(doc, new[]
                     {
                         BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns
                     }))
            {
                ElementId baseLevelId = ElementIdValue(column, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
                ElementId topLevelId = ElementIdValue(column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                if (baseLevelId == ElementId.InvalidElementId || topLevelId == ElementId.InvalidElementId)
                    continue;
                checkedColumns++;
                if (baseLevelId == topLevelId)
                    continue;

                var baseLevel = doc.GetElement(baseLevelId) as Level;
                var topLevel = doc.GetElement(topLevelId) as Level;
                if (baseLevel == null || topLevel == null)
                    continue;

                double low = Math.Min(baseLevel.Elevation, topLevel.Elevation);
                double high = Math.Max(baseLevel.Elevation, topLevel.Elevation);
                int crossed = storeys.Count(l => l.Elevation > low + 1e-6 && l.Elevation <= high + 1e-6);
                if (crossed <= maxSpan)
                    continue;

                spanning.Add(column);
                if (details.Count < 8)
                    details.Add(Label(column) + ": " + baseLevel.Name + " → " + topLevel.Name +
                                " (" + crossed + " storeys)");
            }

            if (spanning.Count == 0)
                return;

            DmFinding finding = PracticeFinding(practice,
                spanning.Count + " column(s) span more than " + maxSpan + " storey",
                "Columns modelled through several storeys: " + string.Join("; ", details) +
                (spanning.Count > details.Count ? " …" : "") + ".");
            finding.CheckedCount = checkedColumns;
            finding.AffectedCount = spanning.Count;
            finding.Categories.Add(BuiltInCategory.OST_StructuralColumns.ToString());
            Add(result, finding, result.ModelTitle, spanning, options);
        }

        // ── RMP-16 · one wall per storey ────────────────────────────────────────

        /// <summary>
        /// The column rule of RMP-02 applied to walls: a wall drawn from the ground floor
        /// straight up to the roof exports as one IfcWall and cannot be assigned to a storey.
        /// Walls with an unconnected height are measured from their geometry instead of a top
        /// constraint, so a free-standing full-height wall is caught as well.
        /// </summary>
        private static void CheckWallStoreys(Document doc, DmAuditResult result, DmAuditOptions options,
                                             DmModellingPractice practice, List<Level> storeys)
        {
            if (storeys.Count < 2)
                return;

            int maxSpan = (int)practice.Number("maximumStoreysSpanned", 1);
            bool ignoreCurtain = practice.Flag("ignoreCurtainWalls", true);
            bool useGeometry = practice.Flag("includeUnconnectedHeight", true);

            var spanning = new List<Element>();
            var details = new List<string>();
            int checkedWalls = 0;

            foreach (Wall wall in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Walls)
                         .WhereElementIsNotElementType()
                         .OfClass(typeof(Wall))
                         .Cast<Wall>())
            {
                if (ignoreCurtain && IsCurtainWall(wall))
                    continue;
                if (!(doc.GetElement(wall.LevelId) is Level baseLevel))
                    continue;
                checkedWalls++;

                double low;
                double high;
                string span;

                ElementId topLevelId = ElementIdValue(wall, BuiltInParameter.WALL_HEIGHT_TYPE);
                if (doc.GetElement(topLevelId) is Level topLevel)
                {
                    low = Math.Min(baseLevel.Elevation, topLevel.Elevation);
                    high = Math.Max(baseLevel.Elevation, topLevel.Elevation);
                    span = baseLevel.Name + " → " + topLevel.Name;
                }
                else
                {
                    // Unconnected height: the geometry is the only thing that says how far it goes.
                    if (!useGeometry)
                        continue;
                    BoundingBoxXYZ? box = SafeBoundingBox(wall);
                    if (box == null)
                        continue;
                    low = box.Min.Z;
                    high = box.Max.Z;
                    span = baseLevel.Name + " → unconnected, " +
                           FeetToMeters(high - low).ToString("F1", CultureInfo.InvariantCulture) + " m tall";
                }

                int crossed = storeys.Count(l => l.Elevation > low + 1e-6 && l.Elevation <= high + 1e-6);
                if (crossed <= maxSpan)
                    continue;

                spanning.Add(wall);
                if (details.Count < 8)
                    details.Add(Label(wall) + ": " + span + " (" + crossed + " storeys)");
            }

            if (spanning.Count == 0)
                return;

            DmFinding finding = PracticeFinding(practice,
                spanning.Count + " of " + checkedWalls + " wall(s) span more than " + maxSpan + " storey",
                "Walls modelled through several storeys: " + string.Join("; ", details) +
                (spanning.Count > details.Count ? " …" : "") + ".");
            finding.CheckedCount = checkedWalls;
            finding.AffectedCount = spanning.Count;
            finding.Categories.Add(BuiltInCategory.OST_Walls.ToString());
            Add(result, finding, result.ModelTitle, spanning, options);
        }

        // ── RMP-03 · room bounding walls and columns ────────────────────────────

        private static void CheckRoomBounding(Document doc, DmAuditResult result, DmAuditOptions options,
                                              DmModellingPractice practice)
        {
            var notBounding = new List<Element>();
            int checkedElements = 0;

            if (practice.Flag("checkWalls", true))
            {
                bool checkCurtain = practice.Flag("checkCurtainWalls", false);
                foreach (Wall wall in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_Walls)
                             .WhereElementIsNotElementType()
                             .OfClass(typeof(Wall))
                             .Cast<Wall>())
                {
                    if (!checkCurtain && IsCurtainWall(wall))
                        continue;
                    checkedElements++;
                    if (IsRoomBounding(doc, wall) == false)
                        notBounding.Add(wall);
                }
            }

            if (practice.Flag("checkColumns", true))
            {
                foreach (Element column in Collect(doc, new[]
                         {
                             BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns
                         }))
                {
                    checkedElements++;
                    if (IsRoomBounding(doc, column) == false)
                        notBounding.Add(column);
                }
            }

            if (notBounding.Count == 0)
                return;

            DmFinding finding = PracticeFinding(practice,
                notBounding.Count + " wall(s) / column(s) are not room bounding",
                "The room boundary runs through these elements instead of stopping at their faces, so the room " +
                "areas include part of the construction and the spaces overlap the columns.");
            finding.CheckedCount = checkedElements;
            finding.AffectedCount = notBounding.Count;
            finding.ParameterName = "Room Bounding";
            finding.SampleValue = "Yes";
            finding.FixData["target"] = "room-bounding";
            finding.Categories.Add(BuiltInCategory.OST_Walls.ToString());
            finding.Categories.Add(BuiltInCategory.OST_StructuralColumns.ToString());
            Add(result, finding, result.ModelTitle, notBounding, options);
        }

        /// <summary>Room bounding state of an element: true, false, or null when it has no such parameter.</summary>
        private static bool? IsRoomBounding(Document doc, Element element)
        {
            Parameter? parameter = element.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING) ??
                                   element.LookupParameter("Room Bounding");
            if (parameter == null)
            {
                Element? type = doc.GetElement(element.GetTypeId());
                parameter = type?.LookupParameter("Room Bounding");
            }
            if (parameter == null || parameter.StorageType != StorageType.Integer)
                return null;
            return parameter.AsInteger() != 0;
        }

        // ── RMP-04 · every enclosed region carries a space ──────────────────────

        private static void CheckSpaceCoverage(Document doc, DmAuditResult result, DmAuditOptions options,
                                               DmModellingPractice practice, List<Level> storeys)
        {
            double minimumCoverage = practice.Number("minimumRoomCoveragePercent", 90);
            double minimumFloorArea = practice.Number("minimumStoreyFloorAreaSquareMetres", 20);

            var roomArea = new Dictionary<long, double>();
            foreach (Room room in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType()
                         .OfClass(typeof(SpatialElement))
                         .OfType<Room>())
            {
                if (room.Area <= 1e-6 || room.LevelId == ElementId.InvalidElementId)
                    continue;
                Accumulate(roomArea, room.LevelId.Value, SquareMeters(room.Area));
            }

            var floorArea = new Dictionary<long, double>();
            foreach (Element floor in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Floors)
                         .WhereElementIsNotElementType())
            {
                if (floor.LevelId == ElementId.InvalidElementId)
                    continue;
                if (IsFinishFloor(doc, floor, practice))
                    continue;
                double area = Value(floor, BuiltInParameter.HOST_AREA_COMPUTED);
                if (area <= 0)
                    continue;
                Accumulate(floorArea, floor.LevelId.Value, SquareMeters(area));
            }

            var uncovered = new List<Element>();
            var details = new List<string>();
            foreach (Level level in storeys)
            {
                if (!floorArea.TryGetValue(level.Id.Value, out double floors) || floors < minimumFloorArea)
                    continue;
                roomArea.TryGetValue(level.Id.Value, out double rooms);
                double coverage = rooms / floors * 100.0;
                if (coverage >= minimumCoverage)
                    continue;

                uncovered.Add(level);
                details.Add(level.Name + ": rooms " + rooms.ToString("F1", CultureInfo.InvariantCulture) +
                            " m² of " + floors.ToString("F1", CultureInfo.InvariantCulture) + " m² floor area (" +
                            coverage.ToString("F0", CultureInfo.InvariantCulture) + "%)");
            }

            if (uncovered.Count == 0)
                return;

            DmFinding finding = PracticeFinding(practice,
                uncovered.Count + " storey(s) have areas that carry no room",
                "Placed rooms cover less than " + minimumCoverage.ToString("F0", CultureInfo.InvariantCulture) +
                "% of the built floor area — " + string.Join("; ", details.Take(8)) +
                (details.Count > 8 ? " …" : "") + ".");
            finding.CheckedCount = storeys.Count;
            finding.AffectedCount = uncovered.Count;
            finding.FixData["target"] = "place-rooms";
            finding.Categories.Add(BuiltInCategory.OST_Rooms.ToString());
            Add(result, finding, result.ModelTitle, uncovered, options);
        }

        /// <summary>
        /// A floor that is a finish rather than the structural slab of the storey: by name, or
        /// by being thinner than the limit and not flagged structural. They are left out of the
        /// storey floor area so a level carrying a slab and its finish is not counted twice.
        /// </summary>
        private static bool IsFinishFloor(Document doc, Element floor, DmModellingPractice practice)
        {
            Element? type = doc.GetElement(floor.GetTypeId());
            if (practice.Matches("floorFinishKeywords", SafeName(type) + " " + SafeName(floor)))
                return true;

            double limit = MetersToFeet(practice.Metres("ignoreFloorsThinnerThanMillimetres", 150));
            if (limit <= 0)
                return false;
            double thickness = FloorThickness(doc, floor);
            return thickness > 0 && thickness < limit &&
                   Value(floor, BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL) < 0.5;
        }

        // ── RMP-05 · site elements on the gate and road levels ──────────────────

        private static void CheckSiteLevels(Document doc, DmAuditResult result, DmAuditOptions options,
                                            DmModellingPractice practice)
        {
            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();

            Level? gateLevel = FirstLevelWithPrefix(levels, practice.List("gateLevelPrefixes"), "GATE");
            Level? roadLevel = FirstLevelWithPrefix(levels, practice.List("roadLevelPrefixes"), "ROAD");
            if (gateLevel == null)
                return;

            var siteCategories = Categories(practice.List("siteCategories"));
            var offGateLevel = new List<Element>();
            var offRoadLevel = new List<Element>();
            int checkedElements = 0;

            foreach (Element element in Collect(doc, siteCategories.ToArray()))
            {
                ElementId levelId = LevelOf(element);
                if (levelId == ElementId.InvalidElementId)
                    continue;
                checkedElements++;

                bool isRoad = element.Category != null &&
                              element.Category.Id.Value == (long)BuiltInCategory.OST_Roads;
                if (isRoad && roadLevel != null)
                {
                    if (levelId != roadLevel.Id)
                        offRoadLevel.Add(element);
                    continue;
                }
                if (levelId != gateLevel.Id)
                    offGateLevel.Add(element);
            }

            // Boundary walls and other hardscape modelled with the wall tool.
            foreach (Wall wall in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Walls)
                         .WhereElementIsNotElementType()
                         .OfClass(typeof(Wall))
                         .Cast<Wall>())
            {
                Element? type = doc.GetElement(wall.GetTypeId());
                if (!practice.Matches("boundaryKeywords", SafeName(type) + " " + SafeName(wall)))
                    continue;
                checkedElements++;
                if (wall.LevelId != gateLevel.Id)
                    offGateLevel.Add(wall);
            }

            if (offGateLevel.Count > 0)
            {
                DmFinding finding = PracticeFinding(practice,
                    offGateLevel.Count + " site / hardscape element(s) are not on \"" + gateLevel.Name + "\"",
                    "External hardscape, landscape and boundary elements hosted on a storey of the building are " +
                    "counted into that storey by DM's building card.");
                finding.CheckedCount = checkedElements;
                finding.AffectedCount = offGateLevel.Count;
                finding.ParameterName = "Level";
                finding.SampleValue = gateLevel.Name;
                finding.FixData["target"] = "rehost";
                finding.FixData["levelId"] = gateLevel.Id.Value.ToString(CultureInfo.InvariantCulture);
                finding.FixData["levelName"] = gateLevel.Name;
                Add(result, finding, result.ModelTitle, offGateLevel, options);
            }

            if (offRoadLevel.Count > 0 && roadLevel != null)
            {
                DmFinding finding = PracticeFinding(practice,
                    offRoadLevel.Count + " external road element(s) are not on \"" + roadLevel.Name + "\"",
                    "External roads outside the plot boundary belong on the road level, not on a storey of the " +
                    "building or on the gate level.");
                finding.CheckedCount = checkedElements;
                finding.AffectedCount = offRoadLevel.Count;
                finding.ParameterName = "Level";
                finding.SampleValue = roadLevel.Name;
                finding.FixData["target"] = "rehost";
                finding.FixData["levelId"] = roadLevel.Id.Value.ToString(CultureInfo.InvariantCulture);
                finding.FixData["levelName"] = roadLevel.Name;
                Add(result, finding, result.ModelTitle, offRoadLevel, options);
            }
        }

        private static Level? FirstLevelWithPrefix(List<Level> levels, IReadOnlyList<string> prefixes, string keyword)
        {
            foreach (Level level in levels.OrderBy(l => l.Elevation))
            {
                string name = level.Name.ToUpperInvariant();
                if (prefixes.Any(p => p.Length > 0 && name.StartsWith(p.ToUpperInvariant(), StringComparison.Ordinal)))
                    return level;
                if (keyword.Length > 0 && name.IndexOf(keyword, StringComparison.Ordinal) >= 0)
                    return level;
            }
            return null;
        }

        // ── RMP-06 · elements on the level they actually sit on ─────────────────

        private static void CheckLevelAssociation(Document doc, DmAuditResult result, DmAuditOptions options,
                                                  DmModellingPractice practice, List<Level> storeys)
        {
            if (storeys.Count < 2)
                return;

            double tolerance = MetersToFeet(practice.Metres("toleranceMillimetres", 500));
            var categories = Categories(practice.List("categories"));
            var wrongLevel = new List<Element>();
            var details = new List<string>();
            var fixData = new Dictionary<long, string>();
            int checkedElements = 0;
            var levelById = storeys.ToDictionary(l => l.Id.Value, l => l);

            foreach (Element element in Collect(doc, categories.ToArray()))
            {
                ElementId levelId = LevelOf(element);
                if (levelId == ElementId.InvalidElementId || !levelById.TryGetValue(levelId.Value, out Level? own))
                    continue;

                BoundingBoxXYZ? box = SafeBoundingBox(element);
                if (box == null)
                    continue;
                checkedElements++;

                // Only elements whose geometry lies entirely outside their own storey are
                // reported: a slab hanging below its level or a wall with an offset is normal.
                double storeyTop = NextStoreyElevation(storeys, own);
                bool entirelyBelow = box.Max.Z < own.Elevation - tolerance;
                bool entirelyAbove = box.Min.Z > storeyTop + tolerance;
                if (!entirelyBelow && !entirelyAbove)
                    continue;

                Level best = BestLevelFor(storeys, box.Min.Z, tolerance);
                if (best.Id == own.Id)
                    continue;

                wrongLevel.Add(element);
                if (details.Count < 8)
                    details.Add(Label(element) + ": on \"" + own.Name + "\", belongs on \"" + best.Name + "\"");

                // The script needs the target level and the elevation difference it has to
                // compensate on the offset so the geometry does not move.
                fixData[element.Id.Value] =
                    best.Id.Value.ToString(CultureInfo.InvariantCulture) + "|" +
                    (own.Elevation - best.Elevation).ToString("R", CultureInfo.InvariantCulture) + "|" +
                    best.Name;
            }

            if (wrongLevel.Count == 0)
                return;

            DmFinding finding = PracticeFinding(practice,
                wrongLevel.Count + " element(s) are hosted on a level they do not sit on",
                "The geometry of these elements is entirely outside the storey of their level parameter — " +
                string.Join("; ", details) + (wrongLevel.Count > details.Count ? " …" : "") + ".");
            finding.CheckedCount = checkedElements;
            finding.AffectedCount = wrongLevel.Count;
            finding.ParameterName = "Level";
            finding.FixData["target"] = "rehost-nearest";
            Add(result, finding, result.ModelTitle, wrongLevel, options);
            foreach (long id in finding.ElementIds)
            {
                if (fixData.TryGetValue(id, out string? data))
                    finding.ElementFixData[id] = data;
            }
        }

        private static Level BestLevelFor(List<Level> storeys, double elevation, double tolerance)
        {
            Level best = storeys[0];
            foreach (Level level in storeys)
            {
                if (level.Elevation <= elevation + tolerance)
                    best = level;
            }
            return best;
        }

        private static double NextStoreyElevation(List<Level> storeys, Level level)
        {
            foreach (Level candidate in storeys)
            {
                if (candidate.Elevation > level.Elevation + 1e-6)
                    return candidate.Elevation;
            }
            return level.Elevation + MetersToFeet(4.0);
        }

        // ── RMP-07 · FFL / SSL level pairs ──────────────────────────────────────

        private static void CheckFinishedAndStructuralLevels(Document doc, DmAuditResult result,
                                                             DmAuditOptions options, DmModellingPractice practice)
        {
            IReadOnlyList<string> finished = practice.List("finishedLevelSuffixes");
            IReadOnlyList<string> structural = practice.List("structuralLevelSuffixes");
            if (finished.Count == 0 || structural.Count == 0)
                return;

            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var finishedLevels = levels.Where(l => EndsWithAny(l.Name, finished)).ToList();
            if (finishedLevels.Count == 0)
                return;   // the model does not use the FFL/SSL convention at all

            var structuralNames = new HashSet<string>(
                levels.Where(l => EndsWithAny(l.Name, structural)).Select(l => BaseLevelName(l.Name, structural)),
                StringComparer.OrdinalIgnoreCase);

            var missing = finishedLevels
                .Where(l => !structuralNames.Contains(BaseLevelName(l.Name, finished)))
                .Cast<Element>()
                .ToList();

            if (missing.Count == 0)
                return;

            DmFinding finding = PracticeFinding(practice,
                missing.Count + " finished floor level(s) have no matching structural (SSL) level",
                "Levels without their SSL counterpart: " +
                string.Join(", ", missing.Take(10).Select(l => l.Name)) +
                (missing.Count > 10 ? " …" : "") + ".");
            finding.CheckedCount = finishedLevels.Count;
            finding.AffectedCount = missing.Count;
            finding.ReferenceData = DmReferenceData.LevelNaming() + PracticeReferenceData(practice);
            Add(result, finding, result.ModelTitle, missing, options);
        }

        private static bool EndsWithAny(string name, IReadOnlyList<string> suffixes)
        {
            string upper = name.Trim().ToUpperInvariant();
            return suffixes.Any(s => s.Length > 0 && upper.EndsWith(s.ToUpperInvariant(), StringComparison.Ordinal));
        }

        private static string BaseLevelName(string name, IReadOnlyList<string> suffixes)
        {
            string trimmed = name.Trim();
            foreach (string suffix in suffixes)
            {
                if (suffix.Length == 0)
                    continue;
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(0, trimmed.Length - suffix.Length).Trim().TrimEnd('_', '-', ' ');
            }
            return trimmed;
        }

        // ── RMP-08 · modelled with the correct tool ─────────────────────────────

        private static void CheckModellingTools(Document doc, DmAuditResult result, DmAuditOptions options,
                                                DmModellingPractice practice)
        {
            int minimumParts = (int)practice.Number("minimumParts", 3);
            var categories = Categories(practice.List("categories"));
            var matched = new List<Element>();
            var byCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (Element element in Collect(doc, categories.ToArray()))
            {
                Element? type = doc.GetElement(element.GetTypeId());
                string name = SafeName(type) + " " + SafeName(element);
                if (!practice.Matches("keywords", name))
                    continue;
                matched.Add(element);
                string category = element.Category?.Name ?? "?";
                byCategory[category] = byCategory.TryGetValue(category, out int n) ? n + 1 : 1;
            }

            if (matched.Count < minimumParts || byCategory.Count < 2)
                return;

            DmFinding finding = PracticeFinding(practice,
                matched.Count + " element(s) of a composite object are modelled with " + byCategory.Count +
                " different tools",
                "Parts found: " +
                string.Join(", ", byCategory.OrderByDescending(p => p.Value).Select(p => p.Value + " × " + p.Key)) +
                ". DM expects one element per object so it exports as a single IFC entity.");
            finding.CheckedCount = matched.Count;
            finding.AffectedCount = matched.Count;
            Add(result, finding, result.ModelTitle, matched, options);
        }

        // ── RMP-09 · finishes export as IfcCovering ─────────────────────────────

        private static void CheckFinishIfcClass(Document doc, DmAuditResult result, DmAuditOptions options,
                                                DmModellingPractice practice)
        {
            string target = practice.Text("targetIfcClass", "IfcCovering");
            double floorLimit = MetersToFeet(practice.Metres("floorFinishMaximumThicknessMillimetres", 150));
            double claddingLimit = MetersToFeet(practice.Metres("claddingMaximumThicknessMillimetres", 100));
            bool useCladdingThickness = practice.Flag("useThicknessForCladding", false);
            bool useFloorThickness = practice.Flag("useThicknessForFloorFinish", true);

            var floors = new List<Element>();
            int checkedFloors = 0;
            foreach (Element floor in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Floors)
                         .WhereElementIsNotElementType())
            {
                checkedFloors++;
                if (ExportsAs(doc, floor, target))
                    continue;
                Element? type = doc.GetElement(floor.GetTypeId());
                bool byName = practice.Matches("floorFinishKeywords", SafeName(type) + " " + SafeName(floor));
                double thickness = FloorThickness(doc, floor);
                bool byThickness = useFloorThickness && thickness > 0 && thickness <= floorLimit &&
                                   Value(floor, BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL) < 0.5;
                if (byName || byThickness)
                    floors.Add(floor);
            }

            if (floors.Count > 0)
            {
                DmFinding finding = PracticeFinding(practice,
                    floors.Count + " floor finish(es) would export as IfcSlab",
                    "These floors read as a finish (name or thickness) but carry no explicit IFC class, so the " +
                    "exporter writes them as IfcSlab and DM counts them as structural slabs.");
                finding.CheckedCount = checkedFloors;
                finding.AffectedCount = floors.Count;
                finding.FixKind = DmFixKind.SetParameter;
                finding.ParameterName = "IfcExportAs";
                finding.SampleValue = target;
                finding.Table = "Covering_Finishes";
                finding.FixData["target"] = "ifc-class";
                finding.FixData["ifcClass"] = target;
                finding.FixData["predefinedType"] = practice.Text("floorPredefinedType", "FLOORING");
                finding.Categories.Add(BuiltInCategory.OST_Floors.ToString());
                Add(result, finding, result.ModelTitle, floors, options);
            }

            var walls = new List<Element>();
            int checkedWalls = 0;
            foreach (Wall wall in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Walls)
                         .WhereElementIsNotElementType()
                         .OfClass(typeof(Wall))
                         .Cast<Wall>())
            {
                if (IsCurtainWall(wall))
                    continue;
                checkedWalls++;
                if (ExportsAs(doc, wall, target))
                    continue;
                Element? type = doc.GetElement(wall.GetTypeId());
                bool byName = practice.Matches("claddingKeywords", SafeName(type) + " " + SafeName(wall));
                bool byThickness = false;
                // A thin wall is usually a partition, not a covering, so thickness alone only
                // counts when the project says so: the name is the reliable signal here.
                if (useCladdingThickness)
                {
                    try
                    {
                        byThickness = wall.Width > 0 && wall.Width <= claddingLimit &&
                                      Value(wall, BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT) < 0.5;
                    }
                    catch
                    {
                        // walls without a simple width (stacked, curtain) are skipped
                    }
                }
                if (byName || byThickness)
                    walls.Add(wall);
            }

            if (walls.Count == 0)
                return;

            DmFinding cladding = PracticeFinding(practice,
                walls.Count + " wall cladding / finish(es) would export as IfcWall",
                "These walls read as cladding or a wall finish but carry no explicit IFC class, so they export " +
                "as IfcWall and are counted as construction rather than as a covering.");
            cladding.CheckedCount = checkedWalls;
            cladding.AffectedCount = walls.Count;
            cladding.FixKind = DmFixKind.SetParameter;
            cladding.ParameterName = "IfcExportAs";
            cladding.SampleValue = target;
            cladding.Table = "Covering_Finishes";
            cladding.FixData["target"] = "ifc-class";
            cladding.FixData["ifcClass"] = target;
            cladding.FixData["predefinedType"] = practice.Text("claddingPredefinedType", "CLADDING");
            cladding.Categories.Add(BuiltInCategory.OST_Walls.ToString());
            Add(result, cladding, result.ModelTitle, walls, options);
        }

        /// <summary>True when the element or its type already declares the wanted IFC class.</summary>
        private static bool ExportsAs(Document doc, Element element, string ifcClass)
        {
            string value = IfcExportAsValue(doc, element);
            return value.Length > 0 &&
                   value.IndexOf(ifcClass, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string IfcExportAsValue(Document doc, Element element)
        {
            Parameter? instance = element.LookupParameter("IfcExportAs");
            if (instance != null && instance.StorageType == StorageType.String)
            {
                string text = instance.AsString() ?? "";
                if (text.Length > 0)
                    return text;
            }
            Element? type = doc.GetElement(element.GetTypeId());
            Parameter? typeParameter = type?.LookupParameter("IfcExportAs") ??
                                       type?.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS);
            return typeParameter != null && typeParameter.StorageType == StorageType.String
                ? typeParameter.AsString() ?? ""
                : "";
        }

        // ── RMP-10 · space height up to the ceiling ─────────────────────────────

        private static void CheckSpaceHeight(Document doc, DmAuditResult result, DmAuditOptions options,
                                             DmModellingPractice practice, List<Level> storeys)
        {
            double ratio = practice.Number("minimumHeightRatioOfStorey", 0.8);
            double tolerance = MetersToFeet(practice.Metres("toleranceMillimetres", 100));
            double defaultHeight = MetersToFeet(practice.Metres("defaultCeilingHeightMillimetres", 2700));

            // Ceilings per level, so each room only tests the ceilings of its own storey.
            var ceilingsByLevel = new Dictionary<long, List<KeyValuePair<BoundingBoxXYZ, double>>>();
            foreach (Element ceiling in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Ceilings)
                         .WhereElementIsNotElementType())
            {
                BoundingBoxXYZ? box = SafeBoundingBox(ceiling);
                if (box == null || ceiling.LevelId == ElementId.InvalidElementId)
                    continue;
                long levelId = ceiling.LevelId.Value;
                if (!ceilingsByLevel.TryGetValue(levelId, out List<KeyValuePair<BoundingBoxXYZ, double>>? list))
                {
                    list = new List<KeyValuePair<BoundingBoxXYZ, double>>();
                    ceilingsByLevel[levelId] = list;
                }
                list.Add(new KeyValuePair<BoundingBoxXYZ, double>(box, box.Min.Z));
            }

            var wrongHeight = new List<Element>();
            var fixData = new Dictionary<long, string>();
            int checkedRooms = 0;

            foreach (Room room in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType()
                         .OfClass(typeof(SpatialElement))
                         .OfType<Room>())
            {
                if (room.Area <= 1e-6 || room.Level == null)
                    continue;
                checkedRooms++;

                double levelElevation = room.Level.Elevation;
                double storeyTop = NextStoreyElevation(storeys, room.Level);
                double storeyHeight = storeyTop - levelElevation;
                double height = room.UnboundedHeight;
                double? ceiling = CeilingHeight(room, ceilingsByLevel, levelElevation);

                double target;
                if (ceiling.HasValue)
                    target = ceiling.Value;
                else if (storeyHeight > 0)
                    target = storeyHeight;
                else
                    target = defaultHeight;

                bool wrong = ceiling.HasValue
                    ? Math.Abs(height - target) > tolerance
                    : storeyHeight > 0 && height < storeyHeight * ratio - tolerance;

                if (!wrong)
                    continue;

                wrongHeight.Add(room);
                fixData[room.Id.Value] = target.ToString("R", CultureInfo.InvariantCulture);
            }

            if (wrongHeight.Count == 0)
                return;

            DmFinding finding = PracticeFinding(practice,
                wrongHeight.Count + " of " + checkedRooms + " room(s) do not reach their ceiling",
                "The room height stops below (or runs past) the ceiling of the room, so the space volume and the " +
                "clear height exported to IfcSpace are wrong.");
            finding.CheckedCount = checkedRooms;
            finding.AffectedCount = wrongHeight.Count;
            finding.ParameterName = "Limit Offset";
            finding.FixData["target"] = "space-height";
            finding.Categories.Add(BuiltInCategory.OST_Rooms.ToString());
            Add(result, finding, result.ModelTitle, wrongHeight, options);
            foreach (long id in finding.ElementIds)
            {
                if (fixData.TryGetValue(id, out string? data))
                    finding.ElementFixData[id] = data;
            }
        }

        private static double? CeilingHeight(Room room,
                                             Dictionary<long, List<KeyValuePair<BoundingBoxXYZ, double>>> ceilings,
                                             double levelElevation)
        {
            if (room.LevelId == ElementId.InvalidElementId ||
                !ceilings.TryGetValue(room.LevelId.Value, out List<KeyValuePair<BoundingBoxXYZ, double>>? list))
                return null;
            if (!(room.Location is LocationPoint point))
                return null;

            XYZ position = point.Point;
            foreach (KeyValuePair<BoundingBoxXYZ, double> ceiling in list)
            {
                BoundingBoxXYZ box = ceiling.Key;
                if (position.X < box.Min.X || position.X > box.Max.X ||
                    position.Y < box.Min.Y || position.Y > box.Max.Y)
                    continue;
                double height = ceiling.Value - levelElevation;
                if (height > 0)
                    return height;
            }
            return null;
        }

        // ── RMP-11 · clashes with the linked models ─────────────────────────────

        private static void CheckLinkClashes(Document doc, DmAuditResult result, DmAuditOptions options,
                                             DmModellingPractice practice)
        {
            List<RevitLinkInstance> links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();
            if (links.Count == 0)
                return;

            int cap = (int)practice.Number("maximumElementsPerSide", 3000);
            double minimumVolume = practice.Number("minimumOverlapVolumeCubicMetres", 0.01);
            double budget = practice.Number("timeBudgetSeconds", 25);
            var categories = Categories(practice.List("categories")).ToArray();

            List<Element> hostElements = Collect(doc, categories).Take(cap).ToList();
            if (hostElements.Count == 0)
                return;

            var clashing = new List<Element>();
            var details = new List<string>();
            var stopwatch = Stopwatch.StartNew();

            foreach (RevitLinkInstance link in links)
            {
                Document? linkDoc = null;
                try
                {
                    linkDoc = link.GetLinkDocument();
                }
                catch
                {
                    // an unloaded link has no document
                }
                if (linkDoc == null)
                    continue;

                Transform transform = link.GetTotalTransform();
                List<Element> linkElements = Collect(linkDoc, categories).Take(cap).ToList();
                if (linkElements.Count == 0)
                    continue;

                var grid = new DmBoxGrid(MetersToFeet(5.0));
                foreach (Element element in linkElements)
                {
                    BoundingBoxXYZ? box = SafeBoundingBox(element);
                    if (box == null)
                        continue;
                    grid.Add(element, Transformed(box, transform));
                }

                foreach (Element host in hostElements)
                {
                    if (stopwatch.Elapsed.TotalSeconds > budget)
                        break;
                    BoundingBoxXYZ? hostBox = SafeBoundingBox(host);
                    if (hostBox == null)
                        continue;

                    foreach (KeyValuePair<Element, BoundingBoxXYZ> candidate in grid.Candidates(hostBox))
                    {
                        double overlap = OverlapVolume(hostBox, candidate.Value);
                        if (overlap <= 0)
                            continue;
                        double cubicMeters = UnitUtils.ConvertFromInternalUnits(overlap, UnitTypeId.CubicMeters);
                        if (cubicMeters < minimumVolume)
                            continue;

                        clashing.Add(host);
                        if (details.Count < 8)
                            details.Add(Label(host) + " ↔ " + linkDoc.Title + " · " + Label(candidate.Key) +
                                        " (" + cubicMeters.ToString("F2", CultureInfo.InvariantCulture) + " m³)");
                        break;
                    }
                }

                if (stopwatch.Elapsed.TotalSeconds > budget)
                    break;
            }

            if (clashing.Count == 0)
                return;

            DmFinding finding = PracticeFinding(practice,
                clashing.Count + " element(s) overlap elements of a linked model",
                "Overlapping volumes found: " + string.Join("; ", details) +
                (clashing.Count > details.Count ? " …" : "") +
                ". Each element must exist once across the submitted IFC files.");
            finding.CheckedCount = hostElements.Count;
            finding.AffectedCount = clashing.Count;
            Add(result, finding, result.ModelTitle, clashing, options);
        }

        private static BoundingBoxXYZ Transformed(BoundingBoxXYZ box, Transform transform)
        {
            var corners = new List<XYZ>
            {
                new XYZ(box.Min.X, box.Min.Y, box.Min.Z), new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
                new XYZ(box.Min.X, box.Max.Y, box.Min.Z), new XYZ(box.Max.X, box.Max.Y, box.Min.Z),
                new XYZ(box.Min.X, box.Min.Y, box.Max.Z), new XYZ(box.Max.X, box.Min.Y, box.Max.Z),
                new XYZ(box.Min.X, box.Max.Y, box.Max.Z), new XYZ(box.Max.X, box.Max.Y, box.Max.Z)
            };
            var moved = corners.Select(transform.OfPoint).ToList();
            return new BoundingBoxXYZ
            {
                Min = new XYZ(moved.Min(p => p.X), moved.Min(p => p.Y), moved.Min(p => p.Z)),
                Max = new XYZ(moved.Max(p => p.X), moved.Max(p => p.Y), moved.Max(p => p.Z))
            };
        }

        private static double OverlapVolume(BoundingBoxXYZ first, BoundingBoxXYZ second)
        {
            double x = Math.Min(first.Max.X, second.Max.X) - Math.Max(first.Min.X, second.Min.X);
            double y = Math.Min(first.Max.Y, second.Max.Y) - Math.Max(first.Min.Y, second.Min.Y);
            double z = Math.Min(first.Max.Z, second.Max.Z) - Math.Max(first.Min.Z, second.Min.Z);
            return x <= 0 || y <= 0 || z <= 0 ? 0 : x * y * z;
        }

        // ── RMP-12 · one room per enclosed region ───────────────────────────────

        private static void CheckOneRoomPerRegion(Document doc, DmAuditResult result, DmAuditOptions options,
                                                  DmModellingPractice practice)
        {
            IReadOnlyList<string> keywords = practice.List("warningKeywords");
            if (keywords.Count == 0)
                return;

            var elements = new List<Element>();
            var seen = new HashSet<long>();
            var messages = new List<string>();

            foreach (FailureMessage warning in doc.GetWarnings())
            {
                string description;
                try
                {
                    description = warning.GetDescriptionText() ?? "";
                }
                catch
                {
                    continue;
                }
                if (!keywords.Any(k => k.Length > 0 &&
                                       description.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                if (messages.Count < 5 && !messages.Contains(description))
                    messages.Add(description);

                foreach (ElementId id in warning.GetFailingElements())
                {
                    Element? element = doc.GetElement(id);
                    if (element == null || !seen.Add(id.Value))
                        continue;
                    elements.Add(element);
                }
            }

            if (elements.Count == 0)
                return;

            DmFinding finding = PracticeFinding(practice,
                elements.Count + " room(s) share an enclosed region or are not enclosed",
                "Revit reports: " + string.Join(" | ", messages) +
                ". Two rooms in one region export as two IfcSpaces with the same boundary, so the area is " +
                "counted twice.");
            finding.CheckedCount = elements.Count;
            finding.AffectedCount = elements.Count;
            finding.FixData["target"] = "list-rooms";
            finding.Categories.Add(BuiltInCategory.OST_Rooms.ToString());
            Add(result, finding, result.ModelTitle, elements, options);
        }

        // ── RMP-13 · elements the submission does not need ──────────────────────

        private static void CheckUnwantedElements(Document doc, DmAuditResult result, DmAuditOptions options,
                                                  DmModellingPractice practice)
        {
            int minimum = (int)practice.Number("minimumCount", 1);

            var unwanted = new List<Element>();
            var byCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Element element in Collect(doc, Categories(practice.List("categories")).ToArray()))
            {
                if (ExportsAs(doc, element, "DontExport"))
                    continue;
                unwanted.Add(element);
                string category = element.Category?.Name ?? "?";
                byCategory[category] = byCategory.TryGetValue(category, out int n) ? n + 1 : 1;
            }

            if (unwanted.Count >= minimum && unwanted.Count > 0)
            {
                DmFinding finding = PracticeFinding(practice,
                    unwanted.Count + " element(s) the BIM standard does not ask for are still exported",
                    "Found: " + string.Join(", ",
                        byCategory.OrderByDescending(p => p.Value).Take(10).Select(p => p.Value + " × " + p.Key)) +
                    ". They enlarge the IFC and slow the upload down without adding permit data.");
                finding.CheckedCount = unwanted.Count;
                finding.AffectedCount = unwanted.Count;
                finding.FixKind = DmFixKind.SetParameter;
                finding.ParameterName = "IfcExportAs";
                finding.SampleValue = "DontExport";
                finding.FixData["target"] = "dont-export";
                Add(result, finding, result.ModelTitle, unwanted, options);
            }

            // Structural content inside an architectural model: each discipline submits its own file.
            var structuralCategories = Categories(practice.List("structuralCategoriesInArchitecturalModel")).ToArray();
            if (structuralCategories.Length == 0)
                return;
            bool looksArchitectural = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Any();
            if (!looksArchitectural)
                return;

            List<Element> structural = Collect(doc, structuralCategories);
            if (structural.Count == 0)
                return;

            DmFinding mixed = PracticeFinding(practice,
                structural.Count + " structural element(s) live in this architectural model",
                "DM expects one IFC per discipline and no element in two files. Structural framing and " +
                "foundations belong in the structural submission model.");
            mixed.Severity = DmSeverity.Warning;
            mixed.FixKind = DmFixKind.Review;
            mixed.CheckedCount = structural.Count;
            mixed.AffectedCount = structural.Count;
            mixed.FixData["target"] = "dont-export";
            Add(result, mixed, result.ModelTitle, structural, options);
        }

        // ── RMP-14 · split levels on one storey ─────────────────────────────────

        private static void CheckSplitLevels(Document doc, DmAuditResult result, DmAuditOptions options,
                                             DmModellingPractice practice, List<Level> storeys)
        {
            double threshold = MetersToFeet(practice.Metres("offsetThresholdMillimetres", 300));
            int minimumGroup = (int)practice.Number("minimumElementsPerOffsetGroup", 2);
            if (storeys.Count == 0)
                return;

            // Elements grouped by level and by their rounded base offset: a group of elements
            // sharing a large offset is a part of the storey sitting at another elevation.
            // Only positive offsets count — a negative base offset is what RMP-01 asks for.
            var groups = new Dictionary<string, List<Element>>(StringComparer.Ordinal);

            foreach (Wall wall in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Walls)
                         .WhereElementIsNotElementType()
                         .OfClass(typeof(Wall))
                         .Cast<Wall>())
            {
                double offset = Value(wall, BuiltInParameter.WALL_BASE_OFFSET);
                if (offset < threshold || wall.LevelId == ElementId.InvalidElementId)
                    continue;
                AddToGroup(groups, wall.LevelId.Value, offset, wall);
            }

            foreach (Room room in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType()
                         .OfClass(typeof(SpatialElement))
                         .OfType<Room>())
            {
                double offset = Value(room, BuiltInParameter.ROOM_LOWER_OFFSET);
                if (offset < threshold || room.LevelId == ElementId.InvalidElementId)
                    continue;
                AddToGroup(groups, room.LevelId.Value, offset, room);
            }

            var affected = new List<Element>();
            var details = new List<string>();
            foreach (KeyValuePair<string, List<Element>> group in groups.OrderByDescending(g => g.Value.Count))
            {
                if (group.Value.Count < minimumGroup)
                    continue;
                affected.AddRange(group.Value);
                if (details.Count >= 6)
                    continue;
                string[] parts = group.Key.Split('|');
                var level = doc.GetElement(new ElementId(long.Parse(parts[0], CultureInfo.InvariantCulture))) as Level;
                details.Add((level?.Name ?? "level " + parts[0]) + " + " +
                            FeetToMeters(double.Parse(parts[1], CultureInfo.InvariantCulture))
                                .ToString("F2", CultureInfo.InvariantCulture) + " m: " +
                            group.Value.Count + " element(s)");
            }

            if (affected.Count == 0)
                return;

            DmFinding finding = PracticeFinding(practice,
                affected.Count + " element(s) sit at a different elevation on their storey",
                "Groups found: " + string.Join("; ", details) +
                ". DM asks for a dummy level (Building Story cleared) for these elevated parts instead of a " +
                "large offset on the storey level.");
            finding.CheckedCount = affected.Count;
            finding.AffectedCount = affected.Count;
            finding.FixData["target"] = "dummy-level";
            Add(result, finding, result.ModelTitle, affected, options);
        }

        private static void AddToGroup(Dictionary<string, List<Element>> groups, long levelId, double offset,
                                       Element element)
        {
            // Round to 50 mm so elements of the same platform land in one group.
            double step = MetersToFeet(0.05);
            double rounded = Math.Round(offset / step) * step;
            string key = levelId.ToString(CultureInfo.InvariantCulture) + "|" +
                         rounded.ToString("R", CultureInfo.InvariantCulture);
            if (!groups.TryGetValue(key, out List<Element>? list))
            {
                list = new List<Element>();
                groups[key] = list;
            }
            list.Add(element);
        }

        // ── RMP-15 · purge and trim before the export ───────────────────────────

        private static void CheckExportPreparation(Document doc, DmAuditResult result, DmAuditOptions options,
                                                   DmModellingPractice practice)
        {
            int maximum = (int)practice.Number("maximumUnusedTypes", 50);

            var usedTypes = new HashSet<long>();
            foreach (Element element in new FilteredElementCollector(doc)
                         .WhereElementIsNotElementType())
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                    usedTypes.Add(typeId.Value);
            }

            var unused = new List<Element>();
            foreach (ElementType type in new FilteredElementCollector(doc)
                         .WhereElementIsElementType()
                         .OfClass(typeof(FamilySymbol))
                         .Cast<ElementType>())
            {
                if (type.Category == null || type.Category.CategoryType != CategoryType.Model)
                    continue;
                if (!usedTypes.Contains(type.Id.Value))
                    unused.Add(type);
            }

            if (unused.Count <= maximum)
                return;

            DmFinding finding = PracticeFinding(practice,
                unused.Count + " loadable family type(s) are not used anywhere in the model",
                "DM asks for the model to be purged before the IFC export: unused types enlarge the file and " +
                "the export takes longer for nothing.");
            finding.CheckedCount = usedTypes.Count + unused.Count;
            finding.AffectedCount = unused.Count;
            finding.FixData["target"] = "purge";
            Add(result, finding, result.ModelTitle, unused, options);
        }

        // ── shared helpers of this phase ────────────────────────────────────────

        /// <summary>Skeleton finding carrying everything the practice data already says.</summary>
        private static DmFinding PracticeFinding(DmModellingPractice practice, string title, string detail)
        {
            return new DmFinding
            {
                Group = DmCheckGroup.ModellingPractices,
                Severity = practice.Severity,
                Scope = practice.Scope.Length > 0 ? practice.Scope : "Modelling",
                Title = practice.Id + " · " + title,
                Detail = detail + "  " + practice.Requirement,
                Reference = practice.Reference,
                FixKind = practice.FixKind,
                FixAction = practice.FixAction,
                PracticeId = practice.Id,
                ReferenceData = PracticeReferenceData(practice)
            };
        }

        /// <summary>The practice itself, carried into the prompt so Claude has DM's own wording.</summary>
        private static string PracticeReferenceData(DmModellingPractice practice)
        {
            var lines = new List<string>
            {
                "DM recommended modelling practice " + practice.Id + " — " + practice.Title,
                "What DM asks for: " + practice.Requirement
            };
            if (practice.RevitSteps.Length > 0)
                lines.Add("In Revit: " + practice.RevitSteps);
            if (practice.McpHint.Length > 0)
                lines.Add("Watch out: " + practice.McpHint);
            return string.Join("\n", lines) + "\n";
        }

        private static List<Level> BuildingStoreys(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .WhereElementIsNotElementType()
                .Cast<Level>()
                .Where(IsBuildingStorey)
                .OrderBy(l => l.Elevation)
                .ToList();
        }

        private static List<BuiltInCategory> Categories(IReadOnlyList<string> names)
        {
            var categories = new List<BuiltInCategory>();
            foreach (string name in names)
            {
                if (Enum.TryParse(name, true, out BuiltInCategory category))
                    categories.Add(category);
            }
            return categories;
        }

        /// <summary>Level an element is associated with, whichever parameter carries it.</summary>
        private static ElementId LevelOf(Element element)
        {
            try
            {
                if (element.LevelId != ElementId.InvalidElementId)
                    return element.LevelId;
            }
            catch
            {
                // some elements have no LevelId at all
            }
            foreach (BuiltInParameter builtIn in new[]
                     {
                         BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                         BuiltInParameter.FAMILY_LEVEL_PARAM,
                         BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                         BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM
                     })
            {
                ElementId id = ElementIdValue(element, builtIn);
                if (id != ElementId.InvalidElementId)
                    return id;
            }
            return ElementId.InvalidElementId;
        }

        private static double Value(Element element, BuiltInParameter builtIn)
        {
            try
            {
                Parameter? parameter = element.get_Parameter(builtIn);
                if (parameter == null || !parameter.HasValue)
                    return 0.0;
                if (parameter.StorageType == StorageType.Double)
                    return parameter.AsDouble();
                if (parameter.StorageType == StorageType.Integer)
                    return parameter.AsInteger();
                return 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        private static ElementId ElementIdValue(Element element, BuiltInParameter builtIn)
        {
            try
            {
                Parameter? parameter = element.get_Parameter(builtIn);
                if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.ElementId)
                    return ElementId.InvalidElementId;
                ElementId id = parameter.AsElementId();
                return id.Value > 0 ? id : ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>Thickness of a floor, read from its type (the instance does not carry it).</summary>
        private static double FloorThickness(Document doc, Element floor)
        {
            Element? type = doc.GetElement(floor.GetTypeId());
            if (type == null)
                return 0.0;
            double thickness = Value(type, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
            if (thickness > 0)
                return thickness;
            Parameter? width = type.LookupParameter("Default Thickness") ?? type.LookupParameter("Thickness");
            return width != null && width.StorageType == StorageType.Double ? width.AsDouble() : 0.0;
        }

        private static string SafeName(Element? element)
        {
            if (element == null)
                return "";
            try
            {
                return element.Name ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static double MetersToFeet(double meters)
        {
            return UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);
        }

        private static double SquareMeters(double internalArea)
        {
            return UnitUtils.ConvertFromInternalUnits(internalArea, UnitTypeId.SquareMeters);
        }

        private static void Accumulate(Dictionary<long, double> map, long key, double value)
        {
            map[key] = map.TryGetValue(key, out double sum) ? sum + value : value;
        }

        private static BoundingBoxXYZ? SafeBoundingBox(Element element)
        {
            try
            {
                return element.get_BoundingBox(null);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Coarse spatial index over bounding boxes, so the link clash check does not compare
    /// every element of the model with every element of the link.
    /// </summary>
    internal sealed class DmBoxGrid
    {
        private readonly double _cell;
        private readonly Dictionary<string, List<KeyValuePair<Element, BoundingBoxXYZ>>> _cells =
            new Dictionary<string, List<KeyValuePair<Element, BoundingBoxXYZ>>>(StringComparer.Ordinal);

        public DmBoxGrid(double cellSize)
        {
            _cell = cellSize > 0 ? cellSize : 1.0;
        }

        public void Add(Element element, BoundingBoxXYZ box)
        {
            foreach (string key in Keys(box))
            {
                if (!_cells.TryGetValue(key, out List<KeyValuePair<Element, BoundingBoxXYZ>>? list))
                {
                    list = new List<KeyValuePair<Element, BoundingBoxXYZ>>();
                    _cells[key] = list;
                }
                list.Add(new KeyValuePair<Element, BoundingBoxXYZ>(element, box));
            }
        }

        public IEnumerable<KeyValuePair<Element, BoundingBoxXYZ>> Candidates(BoundingBoxXYZ box)
        {
            var seen = new HashSet<long>();
            foreach (string key in Keys(box))
            {
                if (!_cells.TryGetValue(key, out List<KeyValuePair<Element, BoundingBoxXYZ>>? list))
                    continue;
                foreach (KeyValuePair<Element, BoundingBoxXYZ> candidate in list)
                {
                    if (seen.Add(candidate.Key.Id.Value))
                        yield return candidate;
                }
            }
        }

        private IEnumerable<string> Keys(BoundingBoxXYZ box)
        {
            int minX = (int)Math.Floor(box.Min.X / _cell);
            int maxX = (int)Math.Floor(box.Max.X / _cell);
            int minY = (int)Math.Floor(box.Min.Y / _cell);
            int maxY = (int)Math.Floor(box.Max.Y / _cell);
            int minZ = (int)Math.Floor(box.Min.Z / _cell);
            int maxZ = (int)Math.Floor(box.Max.Z / _cell);

            // A single element that spans the whole building must not explode the index.
            const int limit = 40;
            maxX = Math.Min(maxX, minX + limit);
            maxY = Math.Min(maxY, minY + limit);
            maxZ = Math.Min(maxZ, minZ + limit);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                        yield return x + ":" + y + ":" + z;
                }
            }
        }
    }
}
