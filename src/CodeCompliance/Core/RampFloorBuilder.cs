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
    /// Strategy (chosen for robustness across Revit 2024-2027):
    /// 1. Every station where the surface creases (ramp start/end, the two
    ///    transition-zone boundaries, path kinks, arc tessellation points) becomes a
    ///    BOUNDARY VERTEX of the floor sketch, so the whole profile is controlled by
    ///    corner vertices only — no interior points are drawn at all.
    /// 2. All vertex elevations are applied through one mechanism,
    ///    SlabShapeEditor.ModifySubElement. Because the meaning of its offset value
    ///    has version/context quirks, the builder first calibrates it on one probe
    ///    vertex (measuring what a known offset actually does), then applies all
    ///    vertices, then verifies every vertex against the computed profile and
    ///    corrects any residual error. If the surface still does not match, it
    ///    throws instead of silently leaving a twisted slab.
    ///
    /// Only a helical path that sweeps far around (plan footprint would overlap
    /// itself) is split into several floors — a Revit sketch cannot self-intersect.
    /// Adjacent pieces share their boundary stations, so the surface stays
    /// continuous.
    /// </summary>
    public static class RampFloorBuilder
    {
        private const double MaxChunkSweep = 170.0 * Math.PI / 180.0; // max plan sweep per floor
        private const double ArcStationStep = 7.5 * Math.PI / 180.0;  // arc tessellation angle
        private const double StationMergeTol = 0.01;                  // m, dedup stations
        private const double VertexMatchTol = 0.10;                   // m, vertex -> station matching
        private const double ElevationTolFt = 0.02;                   // ~6 mm, final verification

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

            List<double> stations = BuildStations(path, calc, location, widthM);
            var created = new List<ElementId>();

            foreach (List<double> chunk in ChunkStations(path, stations, location, widthM))
            {
                created.Add(BuildOneFloor(
                    doc, path, calc, widthM, location, floorTypeId,
                    level, baseZFt, heightOffsetFt, chunk));
            }

            return created;
        }

        /// <summary>
        /// All centreline stations that need a boundary vertex: ramp start/end,
        /// transition-zone boundaries, segment joints, and tessellation stations
        /// along arcs (a floor sketch edge is straight, so arcs become fine chords).
        /// </summary>
        private static List<double> BuildStations(
            RampPath path, RampCalcResult calc, RampLineLocation location, double widthM)
        {
            var set = new SortedSet<double> { 0.0, calc.R };

            foreach (double zone in new[] { calc.X, calc.X + calc.XPrime })
                if (zone > StationMergeTol && zone < calc.R - StationMergeTol)
                    set.Add(zone);

            double offset = 0;
            for (int i = 0; i < path.Segments.Count; i++)
            {
                RampPathSegment seg = path.Segments[i];
                double len = seg.CenterlineLength(location, widthM);
                double segEnd = Math.Min(offset + len, calc.R);

                if (seg is RampArcSegment arc)
                {
                    double step = Clamp(arc.CenterlineRadius(location, widthM) * ArcStationStep, 0.4, 3.0);
                    for (double s = offset + step; s < segEnd - StationMergeTol; s += step)
                        set.Add(s);
                    // The last segment may be extended beyond the drawn path up to R.
                    if (i == path.Segments.Count - 1)
                        for (double s = segEnd + step; s < calc.R - StationMergeTol; s += step)
                            set.Add(s);
                }

                if (offset + len > StationMergeTol && offset + len < calc.R - StationMergeTol)
                    set.Add(offset + len); // joint between segments
                offset += len;
                if (offset >= calc.R)
                    break;
            }

            // Dedup stations closer than the merge tolerance.
            var result = new List<double>();
            foreach (double s in set)
                if (result.Count == 0 || s - result[result.Count - 1] > StationMergeTol)
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
        /// that one sketch would overlap itself in plan (helical ramps). Adjacent
        /// chunks share a boundary station. Straight paths yield a single chunk.
        /// </summary>
        private static IEnumerable<List<double>> ChunkStations(
            RampPath path, List<double> stations, RampLineLocation location, double widthM)
        {
            // Cumulative plan turn (radians) contributed by arcs up to each station.
            double[] turn = new double[stations.Count];
            double offset = 0;
            var arcRanges = new List<(double S0, double S1, double InvR)>();
            foreach (RampPathSegment seg in path.Segments)
            {
                double len = seg.CenterlineLength(location, widthM);
                if (seg is RampArcSegment arc)
                    arcRanges.Add((offset, offset + len, 1.0 / arc.CenterlineRadius(location, widthM)));
                offset += len;
            }
            for (int i = 1; i < stations.Count; i++)
            {
                double s0 = stations[i - 1], s1 = stations[i], sweep = 0;
                foreach ((double a0, double a1, double invR) in arcRanges)
                {
                    double lo = Math.Max(s0, a0), hi = Math.Min(s1, a1);
                    if (hi > lo)
                        sweep += (hi - lo) * invR;
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

        private static ElementId BuildOneFloor(
            Document doc,
            RampPath path,
            RampCalcResult calc,
            double widthM,
            RampLineLocation location,
            ElementId floorTypeId,
            Level level,
            double baseZFt,
            double heightOffsetFt,
            List<double> stations)
        {
            // ── Boundary points: mitered offsets of the centreline polyline ─────
            int n = stations.Count;
            var center = new (double X, double Y)[n];
            for (int i = 0; i < n; i++)
            {
                (double lx, double ly, double rx, double ry) = path.EdgesAt(stations[i], location, widthM);
                center[i] = ((lx + rx) / 2.0, (ly + ry) / 2.0);
            }

            double half = widthM / 2.0;
            var left = new (double X, double Y)[n];
            var right = new (double X, double Y)[n];
            for (int i = 0; i < n; i++)
            {
                (double dx, double dy) = DirectionAt(center, i);
                (double mx, double my, double scale) = MiterNormal(center, i, dx, dy);
                double off = half * scale;
                left[i] = (center[i].X + mx * off, center[i].Y + my * off);
                right[i] = (center[i].X - mx * off, center[i].Y - my * off);
            }

            // ── Sketch loop: up the left edge, across the end, back down the right ─
            var loop = new CurveLoop();
            var loopPts = new List<XYZ>();
            for (int i = 0; i < n; i++)
                loopPts.Add(ToXyz(left[i].X, left[i].Y, level.Elevation));
            for (int i = n - 1; i >= 0; i--)
                loopPts.Add(ToXyz(right[i].X, right[i].Y, level.Elevation));
            for (int i = 0; i < loopPts.Count; i++)
            {
                XYZ a = loopPts[i];
                XYZ b = loopPts[(i + 1) % loopPts.Count];
                if (a.DistanceTo(b) > 0.01)
                    loop.Append(Line.CreateBound(a, b));
            }

            Floor floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorTypeId, level.Id);
            Parameter offsetParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
            if (offsetParam != null && !offsetParam.IsReadOnly)
                offsetParam.Set(heightOffsetFt);
            doc.Regenerate();

            // ── Target elevation per boundary point ─────────────────────────────
            var targets = new List<(double X, double Y, double ZFt)>();
            for (int i = 0; i < n; i++)
            {
                double z = baseZFt + ToFeet(calc.HeightAt(stations[i]));
                targets.Add((left[i].X, left[i].Y, z));
                targets.Add((right[i].X, right[i].Y, z));
            }

            ShapeFloor(doc, floor, targets);
            return floor.Id;
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

        /// <summary>Unit travel direction of the centreline polyline at point i.</summary>
        private static (double X, double Y) DirectionAt((double X, double Y)[] c, int i)
        {
            int a = Math.Max(0, i - 1);
            int b = Math.Min(c.Length - 1, i + 1);
            double dx = c[b].X - c[a].X, dy = c[b].Y - c[a].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9)
                return (1, 0);
            return (dx / len, dy / len);
        }

        /// <summary>
        /// Left-pointing miter normal at point i with its length scale (1 on straight
        /// runs, 1/cos(phi/2) at kinks so the ramp keeps its width, clamped for very
        /// sharp corners).
        /// </summary>
        private static (double X, double Y, double Scale) MiterNormal(
            (double X, double Y)[] c, int i, double dirX, double dirY)
        {
            // Normals of the two adjacent legs.
            (double ax, double ay) = LegDirection(c, Math.Max(0, i - 1), i);
            (double bx, double by) = LegDirection(c, i, Math.Min(c.Length - 1, i + 1));
            double nax = -ay, nay = ax, nbx = -by, nby = bx;
            double mx = nax + nbx, my = nay + nby;
            double mlen = Math.Sqrt(mx * mx + my * my);
            if (mlen < 1e-9)
                return (-dirY, dirX, 1.0); // straight or degenerate
            mx /= mlen;
            my /= mlen;
            double cosHalf = mx * nax + my * nay;
            double scale = cosHalf > 0.4 ? 1.0 / cosHalf : 2.5; // clamp sharp kinks
            return (mx, my, scale);
        }

        private static (double X, double Y) LegDirection((double X, double Y)[] c, int a, int b)
        {
            if (a == b)
            {
                // Endpoint: only one leg exists; reuse it.
                if (b + 1 < c.Length) b = b + 1;
                else a = a - 1;
            }
            double dx = c[b].X - c[a].X, dy = c[b].Y - c[a].Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9)
                return (1, 0);
            return (dx / len, dy / len);
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

        private static XYZ ToXyz(double xM, double yM, double zFt)
            => new XYZ(ToFeet(xM), ToFeet(yM), zFt);

        private static double ToFeet(double meters)
            => UnitUtils.ConvertToInternalUnits(meters, UnitTypeId.Meters);

        private static double FromFeet(double feet)
            => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
    }
}
