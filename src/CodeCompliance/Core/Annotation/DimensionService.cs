using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core.Annotation
{
    /// <summary>
    /// Creates the dimension strings:
    ///  - Plans: grid dimensions (every grid) plus overall dimensions (first↔last grid),
    ///    on the bottom and left of the drawing, and detailed opening dimensions along
    ///    each exterior wall that hosts doors/windows.
    ///  - Sections/elevations: a floor-to-floor level string plus an overall level
    ///    dimension on the right side of the view.
    /// Offsets are paper millimetres (multiplied by the view scale) so the layout
    /// matches drafting practice at any scale. Defaults follow common practice and
    /// are the values to calibrate against the reference sheet.
    /// </summary>
    internal static class DimensionService
    {
        private const double GridDimOffsetMm = 12;     // grid string outside the grid extents
        private const double OverallExtraMm = 8;       // overall runs this much further out
        private const double OpeningDimOffsetMm = 8;   // opening string outside the wall face
        private const double LevelDimOffsetMm = 15;    // level string right of the crop region
        private const double MergeToleranceFt = 0.01;  // references closer than this are duplicates

        // ── Plans: grid + overall dimensions ───────────────────────────────────

        public static void AddGridDimensions(AnnotationContext ctx)
        {
            List<Grid> grids = new FilteredElementCollector(ctx.Doc, ctx.View.Id)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Where(g => g.Curve is Line)
                .ToList();
            if (grids.Count < 2)
            {
                if (ctx.Options.GridDimensions || ctx.Options.OverallDimensions)
                    ctx.Result.Warn("Fewer than two straight grids are visible, so no grid/overall dimensions were created.");
                return;
            }

            foreach (List<Grid> group in GroupParallel(ctx, grids))
            {
                if (group.Count < 2)
                    continue;
                try
                {
                    DimensionGridGroup(ctx, group);
                }
                catch (Exception ex)
                {
                    ctx.Result.Warn("A grid dimension string failed: " + ex.Message);
                }
            }
        }

        /// <summary>Split grids into sets of mutually parallel lines.</summary>
        private static List<List<Grid>> GroupParallel(AnnotationContext ctx, List<Grid> grids)
        {
            var groups = new List<List<Grid>>();
            var directions = new List<XYZ>();
            foreach (Grid grid in grids)
            {
                XYZ direction = Normalized(ctx, ((Line)grid.Curve).Direction);
                int found = -1;
                for (int i = 0; i < directions.Count; i++)
                    if (directions[i].DotProduct(direction) > 0.995)
                        found = i;
                if (found < 0)
                {
                    directions.Add(direction);
                    groups.Add(new List<Grid>());
                    found = groups.Count - 1;
                }
                groups[found].Add(grid);
            }
            return groups;
        }

        /// <summary>Flip a direction so parallel/antiparallel grids land in one group.</summary>
        private static XYZ Normalized(AnnotationContext ctx, XYZ direction)
        {
            double up = direction.DotProduct(ctx.Up);
            double right = direction.DotProduct(ctx.Right);
            if (Math.Abs(up) >= Math.Abs(right))
                return up >= 0 ? direction : direction.Negate();
            return right >= 0 ? direction : direction.Negate();
        }

        private static void DimensionGridGroup(AnnotationContext ctx, List<Grid> group)
        {
            XYZ dir = Normalized(ctx, ((Line)group[0].Curve).Direction);
            XYZ measure = ctx.Normal.CrossProduct(dir).Normalize(); // in-plane, ⊥ grids

            // Collect a (reference, position-along-measure) pair per grid.
            var entries = new List<Tuple<Reference, double>>();
            double minAlong = double.MaxValue; // extent of the grids along their own direction
            double zLevel = 0;
            foreach (Grid grid in group)
            {
                Reference? reference = GridReference(ctx, grid);
                if (reference == null)
                    continue;
                var line = (Line)grid.Curve;
                entries.Add(Tuple.Create(reference, line.Origin.DotProduct(measure)));
                minAlong = Math.Min(minAlong,
                    Math.Min(line.GetEndPoint(0).DotProduct(dir), line.GetEndPoint(1).DotProduct(dir)));
                zLevel = line.Origin.DotProduct(ctx.Normal);
            }
            if (entries.Count < 2)
                return;
            entries.Sort((a, b) => a.Item2.CompareTo(b.Item2));

            // The dimension line sits below/left of where the grids end.
            double stringAlong = minAlong - ctx.Mm(GridDimOffsetMm);
            double overallAlong = stringAlong - ctx.Mm(OverallExtraMm);

            if (ctx.Options.GridDimensions)
            {
                var refs = new ReferenceArray();
                double last = double.NaN;
                foreach (Tuple<Reference, double> entry in entries)
                {
                    if (!double.IsNaN(last) && Math.Abs(entry.Item2 - last) < MergeToleranceFt)
                        continue;
                    refs.Append(entry.Item1);
                    last = entry.Item2;
                }
                if (refs.Size >= 2)
                {
                    Line line = DimLine(ctx, dir, measure, stringAlong, entries.First().Item2, entries.Last().Item2, zLevel);
                    Dimension dim = ctx.Doc.Create.NewDimension(ctx.View, line, refs);
                    ctx.Track(dim, "Grid dimensions");
                    ctx.Occupancy.ReserveElement(dim);
                }
            }

            if (ctx.Options.OverallDimensions && Math.Abs(entries.Last().Item2 - entries.First().Item2) > MergeToleranceFt)
            {
                var refs = new ReferenceArray();
                refs.Append(entries.First().Item1);
                refs.Append(entries.Last().Item1);
                Line line = DimLine(ctx, dir, measure, overallAlong, entries.First().Item2, entries.Last().Item2, zLevel);
                Dimension dim = ctx.Doc.Create.NewDimension(ctx.View, line, refs);
                ctx.Track(dim, "Overall dimensions");
                ctx.Occupancy.ReserveElement(dim);
            }
        }

        /// <summary>Reconstruct a model line from (along-grid, across-grid) coordinates.</summary>
        private static Line DimLine(
            AnnotationContext ctx, XYZ dir, XYZ measure,
            double along, double acrossStart, double acrossEnd, double z)
        {
            XYZ p0 = dir * along + measure * acrossStart + ctx.Normal * z;
            XYZ p1 = dir * along + measure * acrossEnd + ctx.Normal * z;
            return Line.CreateBound(p0, p1);
        }

        /// <summary>The dimensionable reference of a grid: its geometry line in this view.</summary>
        private static Reference? GridReference(AnnotationContext ctx, Grid grid)
        {
            var options = new Options { ComputeReferences = true, View = ctx.View };
            GeometryElement? geometry = grid.get_Geometry(options);
            if (geometry == null)
                return null;
            foreach (GeometryObject obj in geometry)
                if (obj is Line line && line.Reference != null)
                    return line.Reference;
            return null;
        }

        // ── Plans: detailed opening dimensions along exterior walls ─────────────

        public static void AddOpeningDimensions(AnnotationContext ctx)
        {
            List<Wall> walls = new FilteredElementCollector(ctx.Doc, ctx.View.Id)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w.Location is LocationCurve lc && lc.Curve is Line)
                .ToList();

            // Openings grouped by their host wall.
            var openingsByWall = new Dictionary<ElementId, List<FamilyInstance>>();
            foreach (BuiltInCategory category in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows })
            {
                var instances = new FilteredElementCollector(ctx.Doc, ctx.View.Id)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>();
                foreach (FamilyInstance instance in instances)
                {
                    ElementId hostId = instance.Host?.Id ?? ElementId.InvalidElementId;
                    if (hostId == ElementId.InvalidElementId)
                        continue;
                    if (!openingsByWall.TryGetValue(hostId, out List<FamilyInstance>? list))
                    {
                        list = new List<FamilyInstance>();
                        openingsByWall[hostId] = list;
                    }
                    list.Add(instance);
                }
            }

            // Prefer exterior walls; if the model has none marked, dimension any wall with openings.
            List<Wall> exterior = walls.Where(IsExterior).ToList();
            List<Wall> targets = exterior.Count > 0 ? exterior : walls;

            int failures = 0;
            foreach (Wall wall in targets)
            {
                if (!openingsByWall.TryGetValue(wall.Id, out List<FamilyInstance>? openings))
                    continue;
                try
                {
                    DimensionWallOpenings(ctx, wall, openings);
                }
                catch
                {
                    failures++;
                }
            }
            if (failures > 0)
                ctx.Result.Warn(failures + " wall(s) could not get opening dimensions (no usable references).");
        }

        private static bool IsExterior(Wall wall)
        {
            try
            {
                return wall.WallType.Function == WallFunction.Exterior;
            }
            catch
            {
                return false;
            }
        }

        private static void DimensionWallOpenings(AnnotationContext ctx, Wall wall, List<FamilyInstance> openings)
        {
            var location = (LocationCurve)wall.Location;
            var wallLine = (Line)location.Curve;
            XYZ dir = wallLine.Direction;

            var entries = new List<Tuple<Reference, double>>();

            // Wall end faces (the planar faces whose normal runs along the wall).
            var options = new Options { ComputeReferences = true };
            GeometryElement? geometry = wall.get_Geometry(options);
            if (geometry != null)
            {
                foreach (GeometryObject obj in geometry)
                {
                    if (!(obj is Solid solid) || solid.Faces.IsEmpty)
                        continue;
                    foreach (Face face in solid.Faces)
                    {
                        if (!(face is PlanarFace planar) || planar.Reference == null)
                            continue;
                        if (Math.Abs(planar.FaceNormal.DotProduct(dir)) > 0.9)
                            entries.Add(Tuple.Create(planar.Reference, planar.Origin.DotProduct(dir)));
                    }
                }
            }

            // Opening centerlines.
            foreach (FamilyInstance opening in openings)
            {
                if (!(opening.Location is LocationPoint point))
                    continue;
                IList<Reference> refs = opening.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);
                if (refs.Count == 0)
                    continue;
                entries.Add(Tuple.Create(refs[0], point.Point.DotProduct(dir)));
            }

            // Sort along the wall and drop duplicates (both end faces of a layer, etc.).
            entries.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            var refArray = new ReferenceArray();
            double last = double.NaN;
            foreach (Tuple<Reference, double> entry in entries)
            {
                if (!double.IsNaN(last) && Math.Abs(entry.Item2 - last) < MergeToleranceFt)
                    continue;
                refArray.Append(entry.Item1);
                last = entry.Item2;
            }
            if (refArray.Size < 2)
                return;

            // Dimension line just outside the exterior face of the wall.
            XYZ mid = wallLine.Evaluate(0.5, true);
            double halfWidth = wall.Width / 2;
            XYZ offsetPoint = mid + wall.Orientation * (halfWidth + ctx.Mm(OpeningDimOffsetMm));
            double along0 = entries.First().Item2;
            double along1 = entries.Last().Item2;
            double baseAlong = offsetPoint.DotProduct(dir);
            XYZ p0 = offsetPoint + dir * (along0 - baseAlong);
            XYZ p1 = offsetPoint + dir * (along1 - baseAlong);
            if (p0.DistanceTo(p1) < MergeToleranceFt)
                return;

            Dimension dim = ctx.Doc.Create.NewDimension(ctx.View, Line.CreateBound(p0, p1), refArray);
            ctx.Track(dim, "Opening dimensions");
            ctx.Occupancy.ReserveElement(dim);
        }

        // ── Sections / elevations: level dimensions ─────────────────────────────

        public static void AddLevelDimensions(AnnotationContext ctx)
        {
            List<Level> levels = new FilteredElementCollector(ctx.Doc, ctx.View.Id)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();
            if (levels.Count < 2)
            {
                ctx.Result.Warn("Fewer than two levels are visible, so no level dimensions were created.");
                return;
            }

            // Anchor the string to the right edge of the crop region.
            BoundingBoxXYZ crop = ctx.View.CropBox;
            XYZ rightEdge = crop.Transform.OfPoint(new XYZ(crop.Max.X, 0, 0));
            double x = rightEdge.DotProduct(ctx.Right) + ctx.Mm(LevelDimOffsetMm);

            try
            {
                var refs = new ReferenceArray();
                foreach (Level level in levels)
                    refs.Append(level.GetPlaneReference());

                Line line = VerticalLine(ctx, x, levels.First().Elevation, levels.Last().Elevation);
                Dimension dim = ctx.Doc.Create.NewDimension(ctx.View, line, refs);
                ctx.Track(dim, "Level dimensions");
                ctx.Occupancy.ReserveElement(dim);

                if (ctx.Options.OverallDimensions)
                {
                    var overallRefs = new ReferenceArray();
                    overallRefs.Append(levels.First().GetPlaneReference());
                    overallRefs.Append(levels.Last().GetPlaneReference());
                    Line overallLine = VerticalLine(
                        ctx, x + ctx.Mm(OverallExtraMm), levels.First().Elevation, levels.Last().Elevation);
                    Dimension overall = ctx.Doc.Create.NewDimension(ctx.View, overallLine, overallRefs);
                    ctx.Track(overall, "Overall dimensions");
                    ctx.Occupancy.ReserveElement(overall);
                }
            }
            catch (Exception ex)
            {
                ctx.Result.Warn("Level dimensions failed: " + ex.Message);
            }
        }

        /// <summary>A vertical model line at plane coordinate x spanning the two elevations.</summary>
        private static Line VerticalLine(AnnotationContext ctx, double x, double z0, double z1)
        {
            // Sections/elevations look horizontally, so Up is the model Z axis and the
            // view normal supplies the depth position (any depth projects the same).
            XYZ basePoint = ctx.Right * x;
            XYZ p0 = new XYZ(basePoint.X, basePoint.Y, z0);
            XYZ p1 = new XYZ(basePoint.X, basePoint.Y, z1);
            return Line.CreateBound(p0, p1);
        }
    }
}
