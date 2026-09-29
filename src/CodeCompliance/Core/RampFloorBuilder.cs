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
    /// <summary>One created ramp floor with its expected vertex elevations.</summary>
    public sealed class RampFloorPiece
    {
        public RampFloorPiece(ElementId floorId, List<(double X, double Y, double ZFt)> targets)
        {
            FloorId = floorId;
            Targets = targets;
        }

        public ElementId FloorId { get; }
        public List<(double X, double Y, double ZFt)> Targets { get; }
    }

    public static class RampFloorBuilder
    {
        private const double OrientTol = 1e-9;                          // m², crossing test tolerance
        private const double ArcPieceSweepExact = 30.0 * Math.PI / 180.0;  // per true-arc boundary piece
        private const double ArcPieceSweepChord = 10.0 * Math.PI / 180.0;  // per chord in fallback mode
        private const double StationMergeTol = 0.01;                  // m, dedup stations
        private const double VertexMatchTol = 0.10;                   // m, vertex -> station matching
        private const double MinEdgeLen = 0.002;                      // m, skip degenerate edges

        /// <summary>~6 mm: max allowed deviation between vertex and computed profile.</summary>
        public const double ElevationTolFt = 0.02;

        /// <summary>
        /// Must run inside an open transaction. Returns the created floors with
        /// their target elevations (verify again AFTER committing — Revit may
        /// revert a slab shape edit during commit failure processing).
        /// With <paramref name="exactArcEdges"/> the boundary uses true arcs on the
        /// drawn circles; otherwise arcs become fine chords (fallback for paths
        /// whose exact-arc shape edit Revit rejects).
        ///
        /// The ramp always covers exactly the computed run: it spans the path
        /// stations [<paramref name="startStationM"/>, startStation + calc.R].
        /// Stations outside the drawn path continue its geometry, so anchoring at
        /// the drawn end (startStation = drawnLength - R) extends or trims the START
        /// instead of the end.
        /// </summary>
        public static IList<RampFloorPiece> Build(
            Document doc,
            RampPath path,
            RampCalcResult calc,
            double widthM,
            RampLineLocation location,
            ElementId floorTypeId,
            double designOffsetM = 0,
            bool exactArcEdges = true,
            double startStationM = 0)
        {
            Level level = FindBaseLevel(doc, ToFeet(path.BaseElevation));
            double baseZFt = ToFeet(path.BaseElevation);
            double heightOffsetFt = baseZFt - level.Elevation;

            List<(RampPathSegment Seg, double Start, double Len)> ranges =
                SegmentRanges(path, location, widthM, designOffsetM);
            List<double> stations = BuildStations(
                calc, location, widthM, designOffsetM, ranges, exactArcEdges, startStationM);

            var created = new List<RampFloorPiece>();
            foreach (List<double> chunk in ChunkStations(ranges, stations, location, widthM, designOffsetM))
            {
                created.Add(BuildOneFloor(
                    doc, calc, widthM, location, floorTypeId,
                    level, baseZFt, heightOffsetFt, chunk, ranges, designOffsetM, exactArcEdges,
                    startStationM));
            }
            return created;
        }

        /// <summary>
        /// Worst vertex deviation across the pieces, for use AFTER the creating
        /// transaction committed. Returns a huge value when a floor lost its shape
        /// edit entirely (Revit's "Slab Shape Edit failed" commit resolution).
        /// </summary>
        public static double PostCommitWorstError(Document doc, IList<RampFloorPiece> pieces)
        {
            double worst = 0;
            foreach (RampFloorPiece piece in pieces)
            {
                if (!(doc.GetElement(piece.FloorId) is Floor floor))
                    return double.MaxValue;
                List<SlabShapeVertex> vertices = GetVertices(doc, floor, out _);
                if (vertices.Count == 0)
                    return double.MaxValue;
                worst = Math.Max(worst, WorstError(vertices, piece.Targets));
            }
            return worst;
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
        /// along curves (max ~30° of sweep per boundary arc piece). Stations run
        /// from <paramref name="startStation"/> to startStation + calc.R, which may
        /// reach outside the drawn path at either end.
        /// </summary>
        private static List<double> BuildStations(
            RampCalcResult calc, RampLineLocation location, double widthM,
            double designOffset, List<(RampPathSegment Seg, double Start, double Len)> ranges,
            bool exactArcEdges, double startStation)
        {
            double rampStart = startStation, rampEnd = startStation + calc.R;
            var set = new SortedSet<double> { rampStart, rampEnd };
            double pieceSweep = exactArcEdges ? ArcPieceSweepExact : ArcPieceSweepChord;

            foreach (double zone in new[] { calc.X, calc.X + calc.XPrime })
            {
                double station = rampStart + zone;
                if (station > rampStart + StationMergeTol && station < rampEnd - StationMergeTol)
                    set.Add(station);
            }

            for (int i = 0; i < ranges.Count; i++)
            {
                (RampPathSegment seg, double start, double len) = ranges[i];
                // The first and last drawn segments carry the ramp wherever it runs
                // past the drawing, so their station range is open-ended there.
                double from = i == 0 ? Math.Min(start, rampStart) : start;
                double to = i == ranges.Count - 1 ? Math.Max(start + len, rampEnd) : start + len;
                double lo = Math.Max(from, rampStart), hi = Math.Min(to, rampEnd);
                if (hi <= lo)
                    continue;

                double turn = seg.PlanTurn;
                if (turn > 1e-6 && len > 1e-9)
                {
                    // Design-line length per piece of sweep, on this segment's own radius.
                    double step = Clamp(len / turn * pieceSweep, 0.3, 8.0);
                    for (double s = lo + step; s < hi - StationMergeTol; s += step)
                        set.Add(s);
                }

                foreach (double joint in new[] { start, start + len })
                    if (joint > rampStart + StationMergeTol && joint < rampEnd - StationMergeTol)
                        set.Add(joint);
            }

            var result = new List<double>();
            foreach (double s in set)
                if (s >= rampStart - 1e-9 && s <= rampEnd + 1e-9
                    && (result.Count == 0 || s - result[result.Count - 1] > StationMergeTol))
                    result.Add(s);
            if (result.Count == 0)
                result.Add(rampStart);
            result[0] = rampStart;
            if (result[result.Count - 1] < rampEnd - StationMergeTol)
                result.Add(rampEnd);
            else
                result[result.Count - 1] = rampEnd;
            return result;
        }

        /// <summary>
        /// Splits the station list into separate floors ONLY where one sketch would
        /// genuinely cross itself in plan — a helix that runs past a full turn, whose
        /// second loop lies over the first. A U-turn, an S-curve or any mix of
        /// straights and curves that does not cross itself builds as ONE floor
        /// however far it turns: a turn angle alone never forces a split.
        ///
        /// Each floor is grown one station interval at a time and closed just before
        /// its outline would cross itself; the next floor starts at that station, so
        /// the surface stays continuous across the joint.
        /// </summary>
        private static IEnumerable<List<double>> ChunkStations(
            List<(RampPathSegment Seg, double Start, double Len)> ranges, List<double> stations,
            RampLineLocation location, double widthM, double designOffset)
        {
            int n = stations.Count;
            var at = new (double LX, double LY, double RX, double RY)[n];
            var mid = new (double LX, double LY, double RX, double RY)[Math.Max(n - 1, 0)];
            for (int i = 0; i < n; i++)
                at[i] = EdgesOnPath(ranges, stations[i], location, widthM, designOffset);
            for (int i = 0; i < n - 1; i++)
                mid[i] = EdgesOnPath(ranges, (stations[i] + stations[i + 1]) / 2.0, location, widthM, designOffset);

            // Greedy pass: each floor runs as far as it can before crossing itself.
            var bounds = new List<int> { 0 };
            int first = 0;
            while (first < n - 1)
            {
                var body = new List<PlanSegment>(); // left and right edge chords laid so far
                int last = first;
                while (last < n - 1 && FitsWithoutCrossing(body, at, mid, first, last))
                {
                    AddInterval(body, at, mid, last);
                    last++;
                }
                if (last == first)
                    last = first + 1; // one interval always fits — never stall
                bounds.Add(last);
                first = last;
            }

            // A helix split greedily leaves a sliver floor at the end; share the run out
            // evenly across the same number of floors instead, when every piece fits.
            int pieces = bounds.Count - 1;
            if (pieces > 1)
            {
                List<int>? even = EvenBounds(stations, pieces);
                if (even != null && AllFit(even, at, mid))
                    bounds = even;
            }

            for (int k = 0; k < bounds.Count - 1; k++)
                yield return stations.GetRange(bounds[k], bounds[k + 1] - bounds[k] + 1);
        }

        /// <summary>Station indices splitting the run into <paramref name="pieces"/> near-equal lengths.</summary>
        private static List<int>? EvenBounds(List<double> stations, int pieces)
        {
            int n = stations.Count;
            double s0 = stations[0], s1 = stations[n - 1];
            var bounds = new List<int> { 0 };
            for (int j = 1; j < pieces; j++)
            {
                double target = s0 + (s1 - s0) * j / pieces;
                int best = -1;
                for (int i = bounds[bounds.Count - 1] + 1; i < n - 1; i++)
                    if (best < 0 || Math.Abs(stations[i] - target) < Math.Abs(stations[best] - target))
                        best = i;
                if (best < 0)
                    return null; // too few stations to spread out
                bounds.Add(best);
            }
            bounds.Add(n - 1);
            return bounds;
        }

        private static bool AllFit(
            List<int> bounds,
            (double LX, double LY, double RX, double RY)[] at,
            (double LX, double LY, double RX, double RY)[] mid)
        {
            for (int k = 0; k < bounds.Count - 1; k++)
            {
                var body = new List<PlanSegment>();
                for (int i = bounds[k]; i < bounds[k + 1]; i++)
                {
                    if (!FitsWithoutCrossing(body, at, mid, bounds[k], i))
                        return false;
                    AddInterval(body, at, mid, i);
                }
            }
            return true;
        }

        /// <summary>A straight chord of the plan outline, in meters.</summary>
        private readonly struct PlanSegment
        {
            public PlanSegment((double X, double Y) a, (double X, double Y) b)
            {
                A = a;
                B = b;
            }

            public (double X, double Y) A { get; }
            public (double X, double Y) B { get; }
        }

        private static (double LX, double LY, double RX, double RY) EdgesOnPath(
            List<(RampPathSegment Seg, double Start, double Len)> ranges, double s,
            RampLineLocation location, double widthM, double designOffset)
        {
            (RampPathSegment seg, double start, double _) = RangeFor(ranges, s);
            return seg.EdgesAt(s - start, location, widthM, designOffset);
        }

        /// <summary>
        /// The four edge chords of interval i (two per edge, through its midpoint, so a
        /// curve is followed closely enough to tell a real crossing from a near miss).
        /// </summary>
        private static IEnumerable<PlanSegment> IntervalChords(
            (double LX, double LY, double RX, double RY)[] at,
            (double LX, double LY, double RX, double RY)[] mid, int i)
        {
            yield return new PlanSegment((at[i].LX, at[i].LY), (mid[i].LX, mid[i].LY));
            yield return new PlanSegment((mid[i].LX, mid[i].LY), (at[i + 1].LX, at[i + 1].LY));
            yield return new PlanSegment((at[i].RX, at[i].RY), (mid[i].RX, mid[i].RY));
            yield return new PlanSegment((mid[i].RX, mid[i].RY), (at[i + 1].RX, at[i + 1].RY));
        }

        private static void AddInterval(
            List<PlanSegment> body,
            (double LX, double LY, double RX, double RY)[] at,
            (double LX, double LY, double RX, double RY)[] mid, int i)
            => body.AddRange(IntervalChords(at, mid, i));

        /// <summary>
        /// Whether interval <paramref name="next"/> can join the floor that starts at
        /// station <paramref name="first"/> without its outline crossing itself: the
        /// new edge chords must not cross the edges or start cap already laid, and the
        /// end cap they would create must not cross anything either.
        /// </summary>
        private static bool FitsWithoutCrossing(
            List<PlanSegment> body,
            (double LX, double LY, double RX, double RY)[] at,
            (double LX, double LY, double RX, double RY)[] mid,
            int first, int next)
        {
            var startCap = new PlanSegment((at[first].LX, at[first].LY), (at[first].RX, at[first].RY));
            var endCap = new PlanSegment((at[next + 1].LX, at[next + 1].LY), (at[next + 1].RX, at[next + 1].RY));
            var added = new List<PlanSegment>(IntervalChords(at, mid, next));

            for (int i = 0; i < added.Count; i++)
            {
                if (Crosses(added[i], startCap))
                    return false;
                foreach (PlanSegment laid in body)
                    if (Crosses(added[i], laid))
                        return false;
                for (int j = i + 1; j < added.Count; j++)
                    if (Crosses(added[i], added[j]))
                        return false;
            }

            if (next > first && Crosses(endCap, startCap))
                return false;
            foreach (PlanSegment laid in body)
                if (Crosses(endCap, laid))
                    return false;
            return true;
        }

        /// <summary>
        /// True when two chords cross or touch. Touching counts: drawings use round
        /// dimensions, so a leg that crosses another often passes exactly through one
        /// of its station vertices, and a strict test would miss that. Only chords
        /// sharing an endpoint — consecutive pieces of an edge, or an edge and a cap,
        /// which are meant to meet — are left out.
        /// </summary>
        private static bool Crosses(PlanSegment p, PlanSegment q)
        {
            if (Same(p.A, q.A) || Same(p.A, q.B) || Same(p.B, q.A) || Same(p.B, q.B))
                return false;

            double d1 = Orient(q.A, q.B, p.A), d2 = Orient(q.A, q.B, p.B);
            double d3 = Orient(p.A, p.B, q.A), d4 = Orient(p.A, p.B, q.B);
            if ((d1 > OrientTol && d2 > OrientTol) || (d1 < -OrientTol && d2 < -OrientTol))
                return false; // p lies wholly on one side of q
            if ((d3 > OrientTol && d4 > OrientTol) || (d3 < -OrientTol && d4 < -OrientTol))
                return false; // q lies wholly on one side of p

            // They meet, or are collinear: only a real contact if their extents overlap.
            return Math.Min(p.A.X, p.B.X) <= Math.Max(q.A.X, q.B.X) + OrientTol
                && Math.Min(q.A.X, q.B.X) <= Math.Max(p.A.X, p.B.X) + OrientTol
                && Math.Min(p.A.Y, p.B.Y) <= Math.Max(q.A.Y, q.B.Y) + OrientTol
                && Math.Min(q.A.Y, q.B.Y) <= Math.Max(p.A.Y, p.B.Y) + OrientTol;
        }

        private static bool Same((double X, double Y) a, (double X, double Y) b)
            => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

        private static double Orient((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        /// <summary>One boundary piece between two consecutive stations.</summary>
        private sealed class EdgeInterval
        {
            public bool IsArc;
            public (double X, double Y) L0, LM, L1, R0, RM, R1;
        }

        private static RampFloorPiece BuildOneFloor(
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
            double designOffset,
            bool exactArcEdges,
            double startStation)
        {
            int n = stations.Count;

            // ── Edge geometry per interval, from each segment's exact frame ─────
            var intervals = new List<EdgeInterval>();
            for (int i = 0; i < n - 1; i++)
            {
                double sa = stations[i], sb = stations[i + 1];
                (RampPathSegment seg, double segStart, double _) = RangeFor(ranges, (sa + sb) / 2.0);
                double la = sa - segStart, lb = sb - segStart, lm = (la + lb) / 2.0;

                var itv = new EdgeInterval { IsArc = seg.IsArc && exactArcEdges };
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
                // Path stations -> profile stations (the profile always runs 0..R).
                double z0 = baseZFt + ToFeet(calc.HeightAt(stations[i] - startStation));
                double z1 = baseZFt + ToFeet(calc.HeightAt(stations[i + 1] - startStation));
                EdgeInterval itv = intervals[i];
                targets.Add((itv.L0.X, itv.L0.Y, z0));
                targets.Add((itv.R0.X, itv.R0.Y, z0));
                targets.Add((itv.L1.X, itv.L1.Y, z1));
                targets.Add((itv.R1.X, itv.R1.Y, z1));
            }

            // Cross-section split lines at every interior station: they force the
            // slab surface into clean ruled strips between stations, which keeps
            // Revit's shape engine stable on curved boundaries ("Slab Shape Edit
            // failed" otherwise appears on long tangent-arc chains at commit).
            var splits = new List<((double X, double Y) A, (double X, double Y) B)>();
            for (int i = 0; i < intervals.Count - 1; i++)
                splits.Add((intervals[i].L1, intervals[i].R1));

            ShapeFloor(doc, floor, targets, splits);
            return new RampFloorPiece(floor.Id, targets);
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
        private static void ShapeFloor(
            Document doc, Floor floor,
            List<(double X, double Y, double ZFt)> targets,
            List<((double X, double Y) A, (double X, double Y) B)> splits)
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

            // Cross-section creases while the slab is still flat, connecting the
            // paired boundary vertices at each interior station. Each split is
            // best-effort — a failing one only loses that crease.
            foreach (((double X, double Y) a, (double X, double Y) b) in splits)
            {
                SlabShapeVertex va = FindAt(vertices, ToFeet(a.X), ToFeet(a.Y));
                SlabShapeVertex vb = FindAt(vertices, ToFeet(b.X), ToFeet(b.Y));
                if (ReferenceEquals(va, vb))
                    continue;
                try
                {
                    AddSplitLine(editor, va, vb);
                }
                catch
                {
                    // keep going; the vertex pass still sets every elevation
                }
            }
            doc.Regenerate();
            vertices = GetVertices(doc, floor, out editor);

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

        private static void AddSplitLine(SlabShapeEditor editor, SlabShapeVertex a, SlabShapeVertex b)
        {
#if REVIT2024 || REVIT2025
#pragma warning disable CS0618 // DrawSplitLine deprecated in 2025, renamed later
            editor.DrawSplitLine(a, b);
#pragma warning restore CS0618
#else
            editor.AddSplitLine(a, b);
#endif
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
