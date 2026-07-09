using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core
{
    /// <summary>
    /// Creates the ramp as a single native Floor shaped with the slab shape editor
    /// ("Modify Sub Elements"), so the result is a real floor that schedules, tags
    /// and hosts like any other floor.
    ///
    /// Geometry:
    /// - The sketch follows the drawn path exactly: straight parts become Lines and
    ///   curved parts become true Arcs with the real edge radii (long curves are
    ///   split into several arc pieces, each still on the exact circle, so the
    ///   slope profile can be controlled along the curve).
    /// - Mixed paths (straight / curve / straight / curve / ...) build as ONE
    ///   floor; kinked line-line joints are mitered, joints involving an arc get a
    ///   tiny connector edge if the drawn pieces are not tangent.
    /// - Stationing (and therefore the slope profile) runs along the DESIGN LINE:
    ///   the centreline of the innermost lane for multi-lane curved ramps, which is
    ///   the code-governing line — not the overall ramp centre.
    ///
    /// Elevations:
    /// - Every crease station (start/end, transition-zone boundaries, joints, arc
    ///   subdivisions) is a boundary VERTEX of the sketch. All vertex elevations go
    ///   through SlabShapeEditor.ModifySubElement, whose offset semantics are
    ///   calibrated at runtime on a probe vertex; afterwards every vertex is
    ///   verified against the computed profile and corrected, and the command fails
    ///   loudly rather than leaving a wrong surface.
    ///
    /// Only a helical path that sweeps far around (plan footprint would overlap
    /// itself) is split into several floors — a Revit sketch cannot self-intersect.
    /// Adjacent pieces share their boundary stations, so the surface stays
    /// continuous.
    /// </summary>
    public static class RampFloorBuilder
    {
        private const double MaxChunkSweep = 170.0 * Math.PI / 180.0; // max plan sweep per floor
        private const double MaxArcPieceSweep = 30.0 * Math.PI / 180.0; // per boundary arc piece
        private const double StationMergeTol = 0.01;                  // m, dedup stations
        private const double VertexMatchTol = 0.10;                   // m, vertex -> station matching
        private const double ElevationTolFt = 0.02;                   // ~6 mm, final verification
        private const double MinEdgeLen = 0.002;                      // m, skip degenerate edges

        /// <summary>Must run inside an open transaction. Returns the created floor ids.</summary>
        public static IList<ElementId> Build(
            Document doc,
            RampPath path,
            RampCalcResult calc,
            double widthM,
            RampLineLocation location,
            ElementId floorTypeId,
            double designOffsetM = 0)
        {
            Level level = FindBaseLevel(doc, ToFeet(path.BaseElevation));
            double baseZFt = ToFeet(path.BaseElevation);
            double heightOffsetFt = baseZFt - level.Elevation;

            List<(RampPathSegment Seg, double Start, double Len)> ranges =
                SegmentRanges(path, location, widthM, designOffsetM);
            List<double> stations = BuildStations(path, calc, location, widthM, designOffsetM, ranges);

            var created = new List<ElementId>();
            foreach (List<double> chunk in ChunkStations(ranges, stations))
            {
                created.Add(BuildOneFloor(
                    doc, calc, widthM, location, floorTypeId,
                    level, baseZFt, heightOffsetFt, chunk, ranges, designOffsetM));
            }
            return created;
        }

        private static List<(RampPathSegment Seg, double Start, double Len)> SegmentRanges(
            RampPath path, RampLineLocation location, double widthM, double designOffset)
        {
            var ranges = new List<(RampPathSegment, double, double)>();
            double offset = 0;
            foreach (RampPathSegment seg in path.Segments)
            {
                double len = seg.CenterlineLength(location, widthM, designOffset);
                ranges.Add((seg, offset, len));
                offset += len;
            }
            return ranges;
        }

        /// <summary>
        /// All design-line stations that need a boundary vertex: ramp start/end,
        /// transition-zone boundaries, segment joints, and subdivision stations
        /// along arcs (max ~30° of sweep per boundary arc piece).
        /// </summary>
        private static List<double> BuildStations(
            RampPath path, RampCalcResult calc, RampLineLocation location, double widthM,
            double designOffset, List<(RampPathSegment Seg, double Start, double Len)> ranges)
        {
            var set = new SortedSet<double> { 0.0, calc.R };

            foreach (double zone in new[] { calc.X, calc.X + calc.XPrime })
                if (zone > StationMergeTol && zone < calc.R - StationMergeTol)
                    set.Add(zone);

            for (int i = 0; i < ranges.Count; i++)
            {
                (RampPathSegment seg, double start, double len) = ranges[i];
                double segEnd = Math.Min(start + len, calc.R);
                bool last = i == ranges.Count - 1;

                if (seg is RampArcSegment arc)
                {
                    double step = Clamp(
                        arc.DesignRadius(location, widthM, designOffset) * MaxArcPieceSweep, 0.4, 8.0);
                    double subdivEnd = last ? calc.R : segEnd; // last arc may extend to R
                    for (double s = start + step; s < subdivEnd - StationMergeTol; s += step)
                        if (s > StationMergeTol)
                            set.Add(s);
                }

                if (start + len > StationMergeTol && start + len < calc.R - StationMergeTol)
                    set.Add(start + len); // joint between segments
                if (start >= calc.R)
                    break;
            }

            var result = new List<double>();
            foreach (double s in set)
                if (s <= calc.R + 1e-9 && (result.Count == 0 || s - result[result.Count - 1] > StationMergeTol))
                    result.Add(s);
            if (result[result.Count - 1] < calc.R - StationMergeTol)
                result.Add(calc.R);
            else
                result[result.Count - 1] = calc.R;
            result[0] = 0.0;
            return result;
        }

        /// <summary>
        /// Splits the station list into floor pieces only when the path turns so far
        /// that one sketch would overlap itself in plan (helical ramps). Straight
        /// and moderately curved paths yield a single chunk = one floor slab.
        /// </summary>
        private static IEnumerable<List<double>> ChunkStations(
            List<(RampPathSegment Seg, double Start, double Len)> ranges, List<double> stations)
        {
            double[] turn = new double[stations.Count];
            for (int i = 1; i < stations.Count; i++)
            {
                double s0 = stations[i - 1], s1 = stations[i], sweep = 0;
                foreach ((RampPathSegment seg, double start, double len) in ranges)
                {
                    if (!(seg is RampArcSegment arc))
                        continue;
                    double rd = arc.DrawnRadius; // sweep angle is radius-independent along the same arc
                    double lo = Math.Max(s0, start), hi = Math.Min(s1, start + len);
                    if (hi > lo && len > 1e-9)
                        sweep += (hi - lo) / len * (arc.DrawnLength / rd);
                }
                turn[i] = turn[i - 1] + sweep;
            }

            var chunk = new List<double> { stations[0] };
            double chunkStartTurn = 0;
            for (int i = 1; i < stations.Count; i++)
            {
                if (turn[i] - chunkStartTurn > MaxChunkSweep && chunk.Count >= 2)
                {
                    yield return chunk;
                    chunk = new List<double> { stations[i - 1] };
                    chunkStartTurn = turn[i - 1];
                }
                chunk.Add(stations[i]);
            }
            if (chunk.Count >= 2)
                yield return chunk;
        }

        /// <summary>One boundary piece between two consecutive stations.</summary>
        private sealed class EdgeInterval
        {
            public bool IsArc;
            public (double X, double Y) L0, LM, L1, R0, RM, R1;
        }

        private static ElementId BuildOneFloor(
            Document doc,
            RampCalcResult calc,
            double widthM,
            RampLineLocation location,
            ElementId floorTypeId,
            Level level,
            double baseZFt,
            double heightOffsetFt,
            List<double> stations,
            List<(RampPathSegment Seg, double Start, double Len)> ranges,
            double designOffset)
        {
            int n = stations.Count;

            // ── Edge geometry per interval, from each segment's exact frame ─────
            var intervals = new List<EdgeInterval>();
            for (int i = 0; i < n - 1; i++)
            {
                double sa = stations[i], sb = stations[i + 1];
                (RampPathSegment seg, double segStart, double _) = RangeFor(ranges, (sa + sb) / 2.0);
                double la = sa - segStart, lb = sb - segStart, lm = (la + lb) / 2.0;

                var itv = new EdgeInterval { IsArc = seg.IsArc };
                (itv.L0.X, itv.L0.Y, itv.R0.X, itv.R0.Y) = seg.EdgesAt(la, location, widthM, designOffset);
                (itv.L1.X, itv.L1.Y, itv.R1.X, itv.R1.Y) = seg.EdgesAt(lb, location, widthM, designOffset);
                (itv.LM.X, itv.LM.Y, itv.RM.X, itv.RM.Y) = seg.EdgesAt(lm, location, widthM, designOffset);
                intervals.Add(itv);
            }

            // ── Miter kinked line-line joints so they share one clean vertex ────
            for (int j = 1; j < intervals.Count; j++)
            {
                EdgeInterval a = intervals[j - 1], b = intervals[j];
                if (a.IsArc || b.IsArc)
                    continue; // arcs must keep their exact endpoints on the circle
                a.L1 = b.L0 = Miter(a.L0, a.L1, b.L0, b.L1, widthM);
                a.R1 = b.R0 = Miter(a.R0, a.R1, b.R0, b.R1, widthM);
            }

            // ── Sketch loop: up the left edge, across the end, back down the right ─
            double zSketch = level.Elevation;
            var curves = new List<Curve>();
            XYZ loopStart = ToXyz(intervals[0].L0, zSketch);
            XYZ cursor = loopStart;
            foreach (EdgeInterval itv in intervals)
                cursor = AppendEdge(curves, cursor, itv.L0, itv.LM, itv.L1, itv.IsArc, zSketch);
            cursor = AppendLine(curves, cursor, ToXyz(intervals[intervals.Count - 1].R1, zSketch));
            for (int i = intervals.Count - 1; i >= 0; i--)
            {
                EdgeInterval itv = intervals[i];
                cursor = AppendEdge(curves, cursor, itv.R1, itv.RM, itv.R0, itv.IsArc, zSketch);
            }
            if (cursor.DistanceTo(loopStart) > ToFeet(MinEdgeLen))
                curves.Add(Line.CreateBound(cursor, loopStart));

            var loop = new CurveLoop();
            foreach (Curve c in curves)
                loop.Append(c);

            Floor floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorTypeId, level.Id);
            Parameter offsetParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
            if (offsetParam != null && !offsetParam.IsReadOnly)
                offsetParam.Set(heightOffsetFt);
            doc.Regenerate();

            // ── Target elevation per boundary vertex position ───────────────────
            var targets = new List<(double X, double Y, double ZFt)>();
            for (int i = 0; i < intervals.Count; i++)
            {
                double z0 = baseZFt + ToFeet(calc.HeightAt(stations[i]));
                double z1 = baseZFt + ToFeet(calc.HeightAt(stations[i + 1]));
                EdgeInterval itv = intervals[i];
                targets.Add((itv.L0.X, itv.L0.Y, z0));
                targets.Add((itv.R0.X, itv.R0.Y, z0));
                targets.Add((itv.L1.X, itv.L1.Y, z1));
                targets.Add((itv.R1.X, itv.R1.Y, z1));
            }

            ShapeFloor(doc, floor, targets);
            return floor.Id;
        }

        private static (RampPathSegment Seg, double Start, double Len) RangeFor(
            List<(RampPathSegment Seg, double Start, double Len)> ranges, double s)
        {
            for (int i = ranges.Count - 1; i >= 0; i--)
                if (s >= ranges[i].Start - 1e-9)
                    return ranges[i];
            return ranges[0];
        }

        /// <summary>
        /// Appends one edge piece (true arc or line). If the previous piece does not
        /// end exactly where this one starts (non-tangent joint at an arc), a small
        /// connector line bridges the gap so the loop stays continuous.
        /// </summary>
        private static XYZ AppendEdge(
            List<Curve> curves, XYZ cursor,
            (double X, double Y) start, (double X, double Y) mid, (double X, double Y) end,
            bool isArc, double zSketch)
        {
            double minLenFt = ToFeet(MinEdgeLen);
            XYZ pStart = ToXyz(start, zSketch);
            XYZ pMid = ToXyz(mid, zSketch);
            XYZ pEnd = ToXyz(end, zSketch);

            if (cursor.DistanceTo(pStart) > minLenFt)
            {
                curves.Add(Line.CreateBound(cursor, pStart));
                cursor = pStart;
            }
            if (cursor.DistanceTo(pEnd) <= minLenFt)
                return cursor;

            if (isArc && pMid.DistanceTo(cursor) > minLenFt && pMid.DistanceTo(pEnd) > minLenFt
                && Line.CreateBound(cursor, pEnd).Distance(pMid) > 1e-5)
            {
                curves.Add(Arc.Create(cursor, pEnd, pMid));
            }
            else
            {
                curves.Add(Line.CreateBound(cursor, pEnd));
            }
            return pEnd;
        }

        private static XYZ AppendLine(List<Curve> curves, XYZ cursor, XYZ target)
        {
            if (cursor.DistanceTo(target) > ToFeet(MinEdgeLen))
            {
                curves.Add(Line.CreateBound(cursor, target));
                return target;
            }
            return cursor;
        }

        /// <summary>
        /// Intersection of two edge lines at a kinked joint; falls back to the
        /// shared point when the legs are parallel or the miter shoots too far.
        /// </summary>
        private static (double X, double Y) Miter(
            (double X, double Y) a0, (double X, double Y) a1,
            (double X, double Y) b0, (double X, double Y) b1,
            double widthM)
        {
            double d1x = a1.X - a0.X, d1y = a1.Y - a0.Y;
            double d2x = b1.X - b0.X, d2y = b1.Y - b0.Y;
            double cross = d1x * d2y - d1y * d2x;
            if (Math.Abs(cross) < 1e-9)
                return a1; // parallel legs: endpoints already coincide
            double t = ((b0.X - a0.X) * d2y - (b0.Y - a0.Y) * d2x) / cross;
            double px = a0.X + t * d1x, py = a0.Y + t * d1y;
            double dev = Math.Sqrt((px - a1.X) * (px - a1.X) + (py - a1.Y) * (py - a1.Y));
            return dev <= 3.0 * widthM ? (px, py) : a1; // clamp extreme spikes
        }

        /// <summary>
        /// Moves every boundary vertex of the (flat) floor to its target elevation
        /// via ModifySubElement, calibrating the meaning of the offset argument at
        /// runtime and verifying the result.
        /// </summary>
        private static void ShapeFloor(Document doc, Floor floor, List<(double X, double Y, double ZFt)> targets)
        {
            SlabShapeEditor editor = floor.GetSlabShapeEditor();
            if (!editor.IsEnabled)
                editor.Enable();
            doc.Regenerate();

            List<SlabShapeVertex> vertices = GetVertices(doc, floor, out editor);
            if (vertices.Count == 0)
                throw new RampCalcException(
                    "Revit did not expose the floor's shape points (the chosen floor type may " +
                    "not support shape editing). Pick a regular floor type and try again.");

            // ── Calibrate: what does ModifySubElement(vertex, offset) actually do? ──
            // The floor is flat, so under any semantics the first call behaves as
            // finalZ = C + offset for some constant C. A second identical call tells
            // apart "offset from a fixed plane" (z stays) from "offset from the
            // current position" (z climbs).
            SlabShapeVertex probe = vertices[0];
            double probeX = probe.Position.X, probeY = probe.Position.Y;
            const double probeOffset = 1.0;

            editor.ModifySubElement(probe, probeOffset);
            doc.Regenerate();
            vertices = GetVertices(doc, floor, out editor);
            SlabShapeVertex probe2 = FindAt(vertices, probeX, probeY);
            double z1 = probe2.Position.Z;
            double planeC = z1 - probeOffset;

            editor.ModifySubElement(probe2, probeOffset);
            doc.Regenerate();
            vertices = GetVertices(doc, floor, out editor);
            double z2 = FindAt(vertices, probeX, probeY).Position.Z;
            bool cumulative = Math.Abs(z2 - (z1 + probeOffset)) < 0.001;

            // ── Apply targets to every vertex (the probe vertex gets fixed too) ──
            ApplyPass(doc, floor, targets, planeC, cumulative, ref editor, ref vertices);

            // ── Verify and correct once, then verify hard ────────────────────────
            double worst = WorstError(vertices, targets);
            if (worst > ElevationTolFt)
            {
                ApplyPass(doc, floor, targets, planeC, cumulative, ref editor, ref vertices);
                worst = WorstError(vertices, targets);
            }
            if (worst > ElevationTolFt)
                throw new RampCalcException(string.Format(
                    "The ramp surface could not be matched to the computed profile " +
                    "(worst deviation {0:F0} mm). No ramp was created.",
                    UnitUtils.ConvertFromInternalUnits(worst, UnitTypeId.Millimeters)));
        }

        private static void ApplyPass(
            Document doc,
            Floor floor,
            List<(double X, double Y, double ZFt)> targets,
            double planeC,
            bool cumulative,
            ref SlabShapeEditor editor,
            ref List<SlabShapeVertex> vertices)
        {
            foreach (SlabShapeVertex vertex in vertices)
            {
                double? target = TargetFor(vertex, targets);
                if (!target.HasValue)
                    continue;
                double pass = cumulative
                    ? target.Value - vertex.Position.Z
                    : target.Value - planeC;
                if (Math.Abs(cumulative ? pass : vertex.Position.Z - target.Value) < 0.0005)
                    continue;
                editor.ModifySubElement(vertex, pass);
            }
            doc.Regenerate();
            vertices = GetVertices(doc, floor, out editor);
        }

        private static double WorstError(List<SlabShapeVertex> vertices, List<(double X, double Y, double ZFt)> targets)
        {
            double worst = 0;
            foreach (SlabShapeVertex vertex in vertices)
            {
                double? target = TargetFor(vertex, targets);
                if (target.HasValue)
                    worst = Math.Max(worst, Math.Abs(vertex.Position.Z - target.Value));
            }
            return worst;
        }

        /// <summary>Target elevation for a vertex, matched by plan position.</summary>
        private static double? TargetFor(SlabShapeVertex vertex, List<(double X, double Y, double ZFt)> targets)
        {
            double vx = FromFeet(vertex.Position.X);
            double vy = FromFeet(vertex.Position.Y);
            double best = double.MaxValue;
            double z = 0;
            foreach ((double x, double y, double zFt) in targets)
            {
                double d = (vx - x) * (vx - x) + (vy - y) * (vy - y);
                if (d < best)
                {
                    best = d;
                    z = zFt;
                }
            }
            return best <= VertexMatchTol * VertexMatchTol ? z : (double?)null;
        }

        private static List<SlabShapeVertex> GetVertices(Document doc, Floor floor, out SlabShapeEditor editor)
        {
            editor = floor.GetSlabShapeEditor();
            var list = new List<SlabShapeVertex>();
            foreach (SlabShapeVertex v in editor.SlabShapeVertices)
                list.Add(v);
            if (list.Count == 0)
            {
                // Shape data can lag one regeneration behind; retry once.
                doc.Regenerate();
                editor = floor.GetSlabShapeEditor();
                foreach (SlabShapeVertex v in editor.SlabShapeVertices)
                    list.Add(v);
            }
            return list;
        }

        private static SlabShapeVertex FindAt(List<SlabShapeVertex> vertices, double xFt, double yFt)
        {
            SlabShapeVertex best = vertices[0];
            double bestD = double.MaxValue;
            foreach (SlabShapeVertex v in vertices)
            {
                double dx = v.Position.X - xFt, dy = v.Position.Y - yFt;
                double d = dx * dx + dy * dy;
                if (d < bestD)
                {
                    bestD = d;
                    best = v;
                }
            }
            return best;
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

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : v > hi ? hi : v;

        private static XYZ ToXyz((double X, double Y) ptM, double zFt)
            => new XYZ(ToFeet(ptM.X), ToFeet(ptM.Y), zFt);

        private static double ToFeet(double meters)
            => UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);

        private static double FromFeet(double feet)
            => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
    }
}
