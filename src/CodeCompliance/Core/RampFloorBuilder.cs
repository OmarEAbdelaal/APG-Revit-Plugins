using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core
{
    /// <summary>
    /// Creates the ramp as native Floor elements shaped with the slab shape editor
    /// ("Modify Sub Elements" points), so the result is real floors that schedule,
    /// tag and host like any other floor.
    ///
    /// The path is split into one floor per straight segment and into arc chunks of
    /// at most 170 degrees per floor (a floor profile cannot self-overlap, and
    /// helical ramps may sweep past 360 degrees). Each floor is created flat at the
    /// base level, then its boundary vertices and added interior points are raised
    /// to the ramp profile (transition + main run + transition) elevations.
    /// </summary>
    public static class RampFloorBuilder
    {
        private const double MaxChunkSweep = 170.0 * Math.PI / 180.0; // per-floor arc sweep
        private const double ArcStationStep = 10.0 * Math.PI / 180.0; // slab-shape point spacing on arcs
        private const double EndMargin = 0.05;                        // m, skip stations this close to floor edges

        /// <summary>Must run inside an open transaction. Returns the created floor ids.</summary>
        public static IList<ElementId> Build(
            Document doc,
            RampPath path,
            RampCalcResult calc,
            double widthM,
            RampLineLocation location,
            ElementId floorTypeId)
        {
            Level level = FindBaseLevel(doc, ToFeet(path.BaseElevation));
            double baseZFt = ToFeet(path.BaseElevation);
            double heightOffsetFt = baseZFt - level.Elevation;

            var created = new List<ElementId>();
            foreach ((RampPathSegment seg, double sGlobal0, double local0, double local1)
                     in Pieces(path, calc, location, widthM))
            {
                double s0 = sGlobal0;                     // global centreline station of piece start
                double s1 = sGlobal0 + (local1 - local0); // global station of piece end

                // Piece corners in plan (meters)
                (double l0x, double l0y, double r0x, double r0y) = seg.EdgesAt(local0, location, widthM);
                (double l1x, double l1y, double r1x, double r1y) = seg.EdgesAt(local1, location, widthM);

                CurveLoop loop;
                if (seg is RampArcSegment arcSeg)
                {
                    double mid = (local0 + local1) / 2.0;
                    (double lmx, double lmy, double rmx, double rmy) = arcSeg.EdgesAt(mid, location, widthM);
                    loop = ArcBandLoop(
                        ToXyz(l0x, l0y, level.Elevation, true), ToXyz(lmx, lmy, level.Elevation, true), ToXyz(l1x, l1y, level.Elevation, true),
                        ToXyz(r0x, r0y, level.Elevation, true), ToXyz(rmx, rmy, level.Elevation, true), ToXyz(r1x, r1y, level.Elevation, true));
                }
                else
                {
                    XYZ pl0 = ToXyz(l0x, l0y, level.Elevation, true);
                    XYZ pl1 = ToXyz(l1x, l1y, level.Elevation, true);
                    XYZ pr0 = ToXyz(r0x, r0y, level.Elevation, true);
                    XYZ pr1 = ToXyz(r1x, r1y, level.Elevation, true);
                    loop = new CurveLoop();
                    loop.Append(Line.CreateBound(pl0, pl1));
                    loop.Append(Line.CreateBound(pl1, pr1));
                    loop.Append(Line.CreateBound(pr1, pr0));
                    loop.Append(Line.CreateBound(pr0, pl0));
                }

                Floor floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorTypeId, level.Id);
                Parameter offsetParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (offsetParam != null && !offsetParam.IsReadOnly)
                    offsetParam.Set(heightOffsetFt);

                // The slab shape editor needs up-to-date geometry.
                doc.Regenerate();

                ShapeFloor(floor, calc, seg, location, widthM,
                    s0, s1, local0, local1, baseZFt,
                    (l0x, l0y), (r0x, r0y), (l1x, l1y), (r1x, r1y));

                created.Add(floor.Id);
            }

            return created;
        }

        /// <summary>
        /// Raises the flat floor to the ramp profile: boundary corner vertices via
        /// ModifySubElement, interior/edge stations via DrawPoint.
        /// </summary>
        private static void ShapeFloor(
            Floor floor,
            RampCalcResult calc,
            RampPathSegment seg,
            RampLineLocation location,
            double widthM,
            double s0,
            double s1,
            double local0,
            double local1,
            double baseZFt,
            (double X, double Y) l0,
            (double X, double Y) r0,
            (double X, double Y) l1,
            (double X, double Y) r1)
        {
            SlabShapeEditor editor = floor.GetSlabShapeEditor();
            if (!editor.IsEnabled)
                editor.Enable();

            // Corner vertices -> profile elevation at their station.
            var corners = new (double X, double Y, double S)[]
            {
                (l0.X, l0.Y, s0), (r0.X, r0.Y, s0),
                (l1.X, l1.Y, s1), (r1.X, r1.Y, s1),
            };
            foreach (SlabShapeVertex vertex in editor.SlabShapeVertices.Cast<SlabShapeVertex>())
            {
                double vx = FromFeet(vertex.Position.X);
                double vy = FromFeet(vertex.Position.Y);
                double best = double.MaxValue;
                double sAt = s0;
                foreach ((double cx, double cy, double sc) in corners)
                {
                    double d = (vx - cx) * (vx - cx) + (vy - cy) * (vy - cy);
                    if (d < best)
                    {
                        best = d;
                        sAt = sc;
                    }
                }
                if (best <= 0.05 * 0.05)
                    editor.ModifySubElement(vertex, ToFeet(calc.HeightAt(sAt)));
            }

            // Interior stations: zone boundaries + curvature stations on arcs.
            var stations = new SortedSet<double>();
            foreach (double zone in new[] { calc.X, calc.X + calc.XPrime })
                if (zone > s0 + EndMargin && zone < s1 - EndMargin)
                    stations.Add(zone);

            if (seg is RampArcSegment arc)
            {
                double step = Math.Max(0.25, arc.CenterlineRadius(location, widthM) * ArcStationStep);
                for (double s = s0 + step; s < s1 - EndMargin; s += step)
                    if (s > s0 + EndMargin)
                        stations.Add(s);
            }

            foreach (double s in stations)
            {
                (double lx, double ly, double rx, double ry) = seg.EdgesAt(local0 + (s - s0), location, widthM);
                double z = baseZFt + ToFeet(calc.HeightAt(s));
                AddShapePoint(editor, ToXyz(lx, ly, z, zIsFeet: true));
                AddShapePoint(editor, ToXyz(rx, ry, z, zIsFeet: true));
            }
        }

        private static void AddShapePoint(SlabShapeEditor editor, XYZ point)
        {
#if REVIT2024 || REVIT2025
            editor.DrawPoint(point); // renamed to AddPoint in Revit 2026
#else
            editor.AddPoint(point);
#endif
        }

        /// <summary>
        /// Splits the path into floor pieces: (segment, global start station, local
        /// start, local end). The last segment is extended when the computed run R
        /// is longer than the drawn path; arcs are chunked so no floor sweeps more
        /// than <see cref="MaxChunkSweep"/>.
        /// </summary>
        private static IEnumerable<(RampPathSegment Seg, double SGlobal0, double Local0, double Local1)>
            Pieces(RampPath path, RampCalcResult calc, RampLineLocation location, double widthM)
        {
            double totalR = calc.R;
            double sGlobal = 0;

            for (int i = 0; i < path.Segments.Count && sGlobal < totalR - 1e-6; i++)
            {
                RampPathSegment seg = path.Segments[i];
                bool last = i == path.Segments.Count - 1;
                double segLen = seg.CenterlineLength(location, widthM);
                double usableLen = last ? Math.Max(segLen, totalR - sGlobal) : segLen;
                double pieceEndLimit = Math.Min(usableLen, totalR - sGlobal);
                if (pieceEndLimit < 0.01)
                    break;

                double chunkLen = pieceEndLimit;
                if (seg is RampArcSegment arc)
                    chunkLen = Math.Max(0.5, arc.CenterlineRadius(location, widthM) * MaxChunkSweep);

                double local = 0;
                while (local < pieceEndLimit - 1e-6)
                {
                    double end = Math.Min(local + chunkLen, pieceEndLimit);
                    // Avoid a sliver floor at the very end of a chunked run.
                    if (pieceEndLimit - end < 0.25 && pieceEndLimit - end > 1e-6)
                        end = pieceEndLimit;
                    yield return (seg, sGlobal + local, local, end);
                    local = end;
                }

                sGlobal += pieceEndLimit;
            }
        }

        private static CurveLoop ArcBandLoop(
            XYZ leftStart, XYZ leftMid, XYZ leftEnd,
            XYZ rightStart, XYZ rightMid, XYZ rightEnd)
        {
            var loop = new CurveLoop();
            loop.Append(Arc.Create(leftStart, leftEnd, leftMid));
            loop.Append(Line.CreateBound(leftEnd, rightEnd));
            loop.Append(Arc.Create(rightEnd, rightStart, rightMid));
            loop.Append(Line.CreateBound(rightStart, leftStart));
            return loop;
        }

        private static Level FindBaseLevel(Document doc, double baseZFt)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();
            if (levels.Count == 0)
                throw new RampCalcException("The model has no levels.");

            Level best = levels[0];
            foreach (Level l in levels)
                if (l.Elevation <= baseZFt + 1e-6)
                    best = l;
            return best;
        }

        private static XYZ ToXyz(double xM, double yM, double z, bool zIsFeet)
            => new XYZ(ToFeet(xM), ToFeet(yM), zIsFeet ? z : ToFeet(z));

        private static double ToFeet(double meters)
            => UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);

        private static double FromFeet(double feet)
            => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
    }
}
