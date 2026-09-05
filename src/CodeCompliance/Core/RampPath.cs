using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core
{
    /// <summary>
    /// One plan segment of the ramp path. All values in meters; the path lives in
    /// plan (XY) — elevations come from the ramp calculation, not from the drawing.
    ///
    /// Stationing (arc-length s) is measured along the DESIGN LINE: the ramp
    /// centreline shifted sideways by <c>designOffset</c> (meters, positive toward
    /// the LEFT of the direction of travel). For multi-lane ramps the design line
    /// is the centreline of the innermost lane, which is the code-governing line
    /// for slope on curved ramps. designOffset = 0 keeps the old behaviour.
    /// </summary>
    public abstract class RampPathSegment
    {
        public abstract bool IsArc { get; }
        public abstract double DrawnLength { get; }
        public abstract (double X, double Y) Start { get; }
        public abstract (double X, double Y) End { get; }
        public abstract RampPathSegment Reversed();

        /// <summary>Design-line length of this segment for the given drawn-line location, total width and design offset.</summary>
        public abstract double CenterlineLength(RampLineLocation location, double width, double designOffset = 0);

        /// <summary>
        /// Left/right ramp edge positions at design-line arc-length s from the
        /// segment start. s may exceed the segment length (used to extend the last
        /// segment).
        /// </summary>
        public abstract (double LX, double LY, double RX, double RY) EdgesAt(
            double s, RampLineLocation location, double width, double designOffset = 0);
    }

    public sealed class RampLineSegment : RampPathSegment
    {
        private readonly double _x0, _y0, _tx, _ty, _len;

        public RampLineSegment(double x0, double y0, double x1, double y1)
        {
            double dx = x1 - x0, dy = y1 - y0;
            _len = Math.Sqrt(dx * dx + dy * dy);
            if (_len < 0.01)
                throw new RampCalcException("A path line is too short (or vertical) in plan.");
            _x0 = x0;
            _y0 = y0;
            _tx = dx / _len;
            _ty = dy / _len;
        }

        public override bool IsArc => false;
        public override double DrawnLength => _len;
        public override (double X, double Y) Start => (_x0, _y0);
        public override (double X, double Y) End => (_x0 + _tx * _len, _y0 + _ty * _len);

        public override RampPathSegment Reversed()
        {
            (double ex, double ey) = End;
            return new RampLineSegment(ex, ey, _x0, _y0);
        }

        // On a straight run every longitudinal line has the same length,
        // so the design offset changes nothing here.
        public override double CenterlineLength(RampLineLocation location, double width, double designOffset = 0)
            => _len;

        public override (double LX, double LY, double RX, double RY) EdgesAt(
            double s, RampLineLocation location, double width, double designOffset = 0)
        {
            double half = width / 2.0;
            double lxv = -_ty, lyv = _tx; // unit left vector
            // Shift the drawn line to the centreline per the chosen location.
            double shift = location switch
            {
                RampLineLocation.Left => -half,   // drawn = left edge, centre is to the right
                RampLineLocation.Right => half,
                _ => 0,
            };
            double cx = _x0 + lxv * shift + _tx * s;
            double cy = _y0 + lyv * shift + _ty * s;
            return (cx + lxv * half, cy + lyv * half, cx - lxv * half, cy - lyv * half);
        }
    }

    /// <summary>
    /// One straight chord of a ramp built from an explicitly drawn outline (left edge +
    /// right edge, of possibly different lengths/shapes). Its left/right edge points at
    /// both ends are already known from the drawing, so <see cref="EdgesAt"/> ignores the
    /// <see cref="RampLineLocation"/>/width/designOffset arguments and just interpolates
    /// between them.
    /// </summary>
    public sealed class RampVariableWidthSegment : RampPathSegment
    {
        private readonly double _lx0, _ly0, _rx0, _ry0;
        private readonly double _lx1, _ly1, _rx1, _ry1;
        private readonly double _len;

        public double Cx0 { get; }
        public double Cy0 { get; }
        public double Cx1 { get; }
        public double Cy1 { get; }
        public double WidthStart { get; }
        public double WidthEnd { get; }

        public RampVariableWidthSegment(
            double lx0, double ly0, double rx0, double ry0,
            double lx1, double ly1, double rx1, double ry1)
        {
            _lx0 = lx0; _ly0 = ly0; _rx0 = rx0; _ry0 = ry0;
            _lx1 = lx1; _ly1 = ly1; _rx1 = rx1; _ry1 = ry1;
            Cx0 = (lx0 + rx0) / 2.0; Cy0 = (ly0 + ry0) / 2.0;
            Cx1 = (lx1 + rx1) / 2.0; Cy1 = (ly1 + ry1) / 2.0;
            WidthStart = Math.Sqrt((rx0 - lx0) * (rx0 - lx0) + (ry0 - ly0) * (ry0 - ly0));
            WidthEnd = Math.Sqrt((rx1 - lx1) * (rx1 - lx1) + (ry1 - ly1) * (ry1 - ly1));
            double dx = Cx1 - Cx0, dy = Cy1 - Cy0;
            _len = Math.Sqrt(dx * dx + dy * dy);
            if (_len < 1e-6)
                _len = 1e-6;
        }

        public override bool IsArc => false;
        public override double DrawnLength => _len;
        public override (double X, double Y) Start => (Cx0, Cy0);
        public override (double X, double Y) End => (Cx1, Cy1);

        public override RampPathSegment Reversed()
            => new RampVariableWidthSegment(_lx1, _ly1, _rx1, _ry1, _lx0, _ly0, _rx0, _ry0);

        public override double CenterlineLength(RampLineLocation location, double width, double designOffset = 0) => _len;

        public override (double LX, double LY, double RX, double RY) EdgesAt(
            double s, RampLineLocation location, double width, double designOffset = 0)
        {
            double t = _len <= 1e-9 ? 0 : s / _len;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            double lx = _lx0 + (_lx1 - _lx0) * t, ly = _ly0 + (_ly1 - _ly0) * t;
            double rx = _rx0 + (_rx1 - _rx0) * t, ry = _ry0 + (_ry1 - _ry0) * t;
            return (lx, ly, rx, ry);
        }
    }

    public sealed class RampArcSegment : RampPathSegment
    {
        private readonly double _cx, _cy, _r, _a0, _sweep;
        private readonly int _dir; // +1 = CCW in plan (centre on the left of travel)

        public RampArcSegment(double cx, double cy, double radius, double startAngle, double sweep, int dir)
        {
            _cx = cx;
            _cy = cy;
            _r = radius;
            _a0 = startAngle;
            _sweep = sweep;
            _dir = dir;
        }

        public bool CenterIsLeftOfTravel => _dir > 0;
        public double DrawnRadius => _r;
        public (double X, double Y) Center => (_cx, _cy);

        public override bool IsArc => true;
        public override double DrawnLength => _r * _sweep;
        public override (double X, double Y) Start => PointAtAngle(_a0, _r);
        public override (double X, double Y) End => PointAtAngle(_a0 + _dir * _sweep, _r);

        public override RampPathSegment Reversed()
            => new RampArcSegment(_cx, _cy, _r, _a0 + _dir * _sweep, _sweep, -_dir);

        /// <summary>Centreline radius after the drawn-line location shift. Moving toward the centre reduces the radius.</summary>
        public double CenterlineRadius(RampLineLocation location, double width)
        {
            double half = width / 2.0;
            double shiftTowardCenter = location switch
            {
                RampLineLocation.Center => 0,
                RampLineLocation.Left => CenterIsLeftOfTravel ? -half : half,
                _ => CenterIsLeftOfTravel ? half : -half,
            };
            return _r - shiftTowardCenter;
        }

        /// <summary>
        /// Radius of the design line (centreline shifted designOffset toward the
        /// left of travel). Toward the arc centre = smaller radius.
        /// </summary>
        public double DesignRadius(RampLineLocation location, double width, double designOffset)
        {
            double rc = CenterlineRadius(location, width);
            return rc + (CenterIsLeftOfTravel ? -designOffset : designOffset);
        }

        public double InnerRadius(RampLineLocation location, double width)
            => CenterlineRadius(location, width) - width / 2.0;

        public override double CenterlineLength(RampLineLocation location, double width, double designOffset = 0)
            => _sweep * DesignRadius(location, width, designOffset);

        /// <summary>Angle (radians) at design-line arc-length s from the segment start.</summary>
        public double AngleAt(double s, RampLineLocation location, double width, double designOffset = 0)
            => _a0 + _dir * s / DesignRadius(location, width, designOffset);

        public (double X, double Y) PointAtAngle(double angle, double radius)
            => (_cx + radius * Math.Cos(angle), _cy + radius * Math.Sin(angle));

        /// <summary>Plan radii of the left and right ramp edges.</summary>
        public (double Left, double Right) EdgeRadii(RampLineLocation location, double width)
        {
            double half = width / 2.0;
            double rc = CenterlineRadius(location, width);
            double rLeft = CenterIsLeftOfTravel ? rc - half : rc + half;
            double rRight = CenterIsLeftOfTravel ? rc + half : rc - half;
            return (rLeft, rRight);
        }

        public override (double LX, double LY, double RX, double RY) EdgesAt(
            double s, RampLineLocation location, double width, double designOffset = 0)
        {
            (double rLeft, double rRight) = EdgeRadii(location, width);
            double a = AngleAt(s, location, width, designOffset);
            double ca = Math.Cos(a), sa = Math.Sin(a);
            return (_cx + rLeft * ca, _cy + rLeft * sa, _cx + rRight * ca, _cy + rRight * sa);
        }
    }

    /// <summary>
    /// The full ramp path: an ordered chain of line/arc segments in plan, built from model
    /// lines/arcs the user drew or selected. Any number of segments — straight, curved, or a
    /// mix of both — chain into a single continuous ramp sketch; the chain is order- and
    /// direction-independent as long as consecutive curves actually touch end to end.
    /// </summary>
    public class RampPath
    {
        private const double JoinTolerance = 0.05; // 5 cm — tolerant of Revit tangent-arc snap slack

        public IReadOnlyList<RampPathSegment> Segments { get; }
        public double BaseElevation { get; }   // meters, from the path start
        public bool HasArc { get; }
        public double DrawnLength { get; }

        /// <summary>True when built from <see cref="FromOutline"/> — left/right edges were
        /// drawn explicitly, so the ramp width may vary along its length.</summary>
        public bool IsVariableWidth { get; }

        private RampPath(
            List<RampPathSegment> segments, double baseElevation,
            bool isVariableWidth = false, bool? forceHasArc = null)
        {
            Segments = segments;
            BaseElevation = baseElevation;
            HasArc = forceHasArc ?? segments.Any(s => s.IsArc);
            DrawnLength = segments.Sum(s => s.DrawnLength);
            IsVariableWidth = isVariableWidth;
        }

        public double CenterlineLength(RampLineLocation location, double width, double designOffset = 0)
            => Segments.Sum(s => s.CenterlineLength(location, width, designOffset));

        /// <summary>
        /// Which side of travel the inner lane is on: +1 = left, -1 = right,
        /// 0 = no arcs (straight ramp — every lane has the same length).
        /// Decided by the tightest arc, which governs compliance.
        /// </summary>
        public int InnerSide(RampLineLocation location, double width)
        {
            RampArcSegment? tightest = null;
            double best = double.MaxValue;
            foreach (RampPathSegment seg in Segments)
                if (seg is RampArcSegment arc)
                {
                    double r = arc.CenterlineRadius(location, width);
                    if (r < best)
                    {
                        best = r;
                        tightest = arc;
                    }
                }
            return tightest == null ? 0 : (tightest.CenterIsLeftOfTravel ? 1 : -1);
        }

        /// <summary>
        /// Signed design-line offset (meters, + = left of travel) putting the
        /// stationing/slope reference on the centreline of the innermost lane.
        /// Zero for straight ramps, single-lane ramps, or a drawn outline (which
        /// has no arc segments to key off — its width already varies as drawn).
        /// </summary>
        public double DesignOffsetFor(RampLineLocation location, double totalWidth, int lanes)
        {
            if (lanes <= 1)
                return 0;
            double laneWidth = totalWidth / lanes;
            return InnerSide(location, totalWidth) * (totalWidth - laneWidth) / 2.0;
        }

        /// <summary>Smallest inner radius among arc segments, or null when the path has no arcs.
        /// For a drawn outline (<see cref="IsVariableWidth"/>) with no true arc segments, falls
        /// back to a discrete curvature estimate from the tessellated centerline.</summary>
        public double? MinInnerRadius(RampLineLocation location, double width)
        {
            double? min = null;
            foreach (RampPathSegment seg in Segments)
                if (seg is RampArcSegment arc)
                {
                    double ri = arc.InnerRadius(location, width);
                    if (!min.HasValue || ri < min.Value)
                        min = ri;
                }
            if (!min.HasValue && IsVariableWidth)
                min = ApproxVariableWidthInnerRadius();
            return min;
        }

        /// <summary>
        /// Approximates the minimum inner-edge radius of a drawn outline by fitting a local
        /// circle through each three consecutive centerline stations (chord / 2*sin(turn/2))
        /// and subtracting the local half-width, since the outline is stored as many small
        /// straight chords rather than true arcs.
        /// </summary>
        private double? ApproxVariableWidthInnerRadius()
        {
            var pts = new List<(double X, double Y, double W)>();
            foreach (RampPathSegment seg in Segments)
                if (seg is RampVariableWidthSegment vw)
                {
                    if (pts.Count == 0)
                        pts.Add((vw.Cx0, vw.Cy0, vw.WidthStart));
                    pts.Add((vw.Cx1, vw.Cy1, vw.WidthEnd));
                }
            if (pts.Count < 3)
                return null;

            double? min = null;
            for (int i = 1; i < pts.Count - 1; i++)
            {
                double v1x = pts[i].X - pts[i - 1].X, v1y = pts[i].Y - pts[i - 1].Y;
                double v2x = pts[i + 1].X - pts[i].X, v2y = pts[i + 1].Y - pts[i].Y;
                double len1 = Math.Sqrt(v1x * v1x + v1y * v1y);
                double len2 = Math.Sqrt(v2x * v2x + v2y * v2y);
                if (len1 < 1e-6 || len2 < 1e-6)
                    continue;
                double turn = Math.Atan2(v1x * v2y - v1y * v2x, v1x * v2x + v1y * v2y);
                if (Math.Abs(turn) < 1e-4)
                    continue; // effectively straight here
                double chord = (len1 + len2) / 2.0;
                double rc = chord / (2.0 * Math.Sin(Math.Abs(turn) / 2.0));
                double ri = rc - pts[i].W / 2.0;
                if (!min.HasValue || ri < min.Value)
                    min = ri;
            }
            return min;
        }

        /// <summary>Smallest left/right edge separation anywhere along a drawn outline (meters).
        /// Zero when the path is not variable-width.</summary>
        public double MinWidthAlongPath()
        {
            double min = double.MaxValue;
            foreach (RampPathSegment seg in Segments)
                if (seg is RampVariableWidthSegment vw)
                {
                    if (vw.WidthStart < min) min = vw.WidthStart;
                    if (vw.WidthEnd < min) min = vw.WidthEnd;
                }
            return min == double.MaxValue ? 0 : min;
        }

        /// <summary>Largest left/right edge separation anywhere along a drawn outline (meters).
        /// Zero when the path is not variable-width.</summary>
        public double MaxWidthAlongPath()
        {
            double max = 0;
            foreach (RampPathSegment seg in Segments)
                if (seg is RampVariableWidthSegment vw)
                {
                    if (vw.WidthStart > max) max = vw.WidthStart;
                    if (vw.WidthEnd > max) max = vw.WidthEnd;
                }
            return max;
        }

        /// <summary>
        /// Design-line radius when the whole path is one arc (the helical case where
        /// the ramp may loop past 360 degrees); null otherwise.
        /// </summary>
        public double? SingleArcDesignRadius(RampLineLocation location, double width, double designOffset = 0)
            => Segments.Count == 1 && Segments[0] is RampArcSegment arc
                ? arc.DesignRadius(location, width, designOffset)
                : (double?)null;

        /// <summary>Centreline radius when the whole path is one arc; null otherwise.</summary>
        public double? SingleArcCenterlineRadius(RampLineLocation location, double width)
            => Segments.Count == 1 && Segments[0] is RampArcSegment arc
                ? arc.CenterlineRadius(location, width)
                : (double?)null;

        /// <summary>
        /// Left/right edges at global design-line arc-length s. Beyond the drawn
        /// path the last segment is extended (straight on, or continuing around its
        /// circle).
        /// </summary>
        public (double LX, double LY, double RX, double RY) EdgesAt(
            double s, RampLineLocation location, double width, double designOffset = 0)
        {
            double offset = 0;
            for (int i = 0; i < Segments.Count; i++)
            {
                double len = Segments[i].CenterlineLength(location, width, designOffset);
                bool last = i == Segments.Count - 1;
                if (s <= offset + len + 1e-9 || last)
                    return Segments[i].EdgesAt(s - offset, location, width, designOffset);
                offset += len;
            }
            throw new InvalidOperationException("Empty ramp path.");
        }

        /// <summary>
        /// Chains the selected model curves into a continuous path. The first curve's
        /// drawn direction sets the direction of travel (going up the ramp).
        /// </summary>
        public static RampPath FromCurves(IList<Curve> curves)
        {
            if (curves.Count == 0)
                throw new RampCalcException("No lines selected.");

            var segs = new List<RampPathSegment>();
            double? baseZ = null;
            foreach (Curve c in curves)
            {
                segs.Add(ToSegment(c));
                double z = FromFeet(c.GetEndPoint(0).Z);
                if (!baseZ.HasValue || z < baseZ.Value)
                    baseZ = z;
            }

            if (segs.Count == 1)
                return new RampPath(segs, baseZ!.Value);

            // Greedy chaining: grow forward from the first curve's end and backward
            // from its start, reversing segments as needed to connect.
            var chain = new LinkedList<RampPathSegment>();
            chain.AddFirst(segs[0]);
            var remaining = new List<RampPathSegment>(segs.Skip(1));

            bool progress = true;
            while (remaining.Count > 0 && progress)
            {
                progress = false;
                (double ex, double ey) = chain.Last!.Value.End;
                (double sx, double sy) = chain.First!.Value.Start;
                for (int i = 0; i < remaining.Count; i++)
                {
                    RampPathSegment seg = remaining[i];
                    if (Near(seg.Start, (ex, ey)))
                    {
                        chain.AddLast(seg);
                    }
                    else if (Near(seg.End, (ex, ey)))
                    {
                        chain.AddLast(seg.Reversed());
                    }
                    else if (Near(seg.End, (sx, sy)))
                    {
                        chain.AddFirst(seg);
                    }
                    else if (Near(seg.Start, (sx, sy)))
                    {
                        chain.AddFirst(seg.Reversed());
                    }
                    else
                    {
                        continue;
                    }
                    remaining.RemoveAt(i);
                    progress = true;
                    break;
                }
            }

            if (remaining.Count > 0)
                throw new RampCalcException(
                    "The selected lines/arcs do not form one continuous path — " +
                    $"{remaining.Count} of them don't connect to the rest within {JoinTolerance * 100:F0} cm. " +
                    "Check for a gap or overlap where a straight segment meets a curve, and that every " +
                    "line/arc is included in the selection.");

            return new RampPath(chain.ToList(), baseZ!.Value);
        }

        /// <summary>
        /// Builds a variable-width ramp path from an explicitly drawn outline: independent
        /// left-edge and right-edge curve chains (each chained the same way as
        /// <see cref="FromCurves"/>, so either edge may itself be several lines/arcs), plus
        /// start/end lines used only to validate the outline closes up. The two edges are
        /// resampled at equal normalized arc-length fractions (a "loft" between rails of
        /// possibly different lengths/shapes) into a chain of small straight chords, each
        /// carrying its own left/right edge points — so the ramp width can vary along the run.
        /// </summary>
        public static RampPath FromOutline(
            IList<Curve> leftCurves, IList<Curve> rightCurves,
            IList<Curve> startCurves, IList<Curve> endCurves)
        {
            if (leftCurves.Count == 0 || rightCurves.Count == 0)
                throw new RampCalcException("Select at least one line/arc for both the left and right edges.");

            RampPath left = FromCurves(leftCurves);
            RampPath right = FromCurves(rightCurves);

            // Orient the right edge to run the same direction of travel as the left edge.
            (double lsx, double lsy) = left.Segments[0].Start;
            (double rsx, double rsy) = right.Segments[0].Start;
            (double rex, double rey) = right.Segments[right.Segments.Count - 1].End;
            if (Dist(lsx, lsy, rex, rey) < Dist(lsx, lsy, rsx, rsy))
                right = Reverse(right);

            (double X, double Y) leftStart = left.Segments[0].Start;
            (double X, double Y) leftEnd = left.Segments[left.Segments.Count - 1].End;
            (double X, double Y) rightStart = right.Segments[0].Start;
            (double X, double Y) rightEnd = right.Segments[right.Segments.Count - 1].End;

            ValidateCap(startCurves, leftStart, rightStart, "start");
            ValidateCap(endCurves, leftEnd, rightEnd, "end");

            double lLen = left.CenterlineLength(RampLineLocation.Center, 0);
            double rLen = right.CenterlineLength(RampLineLocation.Center, 0);
            if (lLen < 0.05 || rLen < 0.05)
                throw new RampCalcException("The left or right edge is too short.");

            int n = Math.Max(12, (int)Math.Ceiling(Math.Max(lLen, rLen) / 0.75));
            var lp = new (double X, double Y)[n + 1];
            var rp = new (double X, double Y)[n + 1];
            for (int i = 0; i <= n; i++)
            {
                (double lx, double ly, _, _) = left.EdgesAt(lLen * i / n, RampLineLocation.Center, 0);
                (double rx, double ry, _, _) = right.EdgesAt(rLen * i / n, RampLineLocation.Center, 0);
                lp[i] = (lx, ly);
                rp[i] = (rx, ry);
            }

            var segs = new List<RampPathSegment>();
            for (int i = 0; i < n; i++)
                segs.Add(new RampVariableWidthSegment(
                    lp[i].X, lp[i].Y, rp[i].X, rp[i].Y,
                    lp[i + 1].X, lp[i + 1].Y, rp[i + 1].X, rp[i + 1].Y));

            double baseZ = Math.Min(left.BaseElevation, right.BaseElevation);
            bool hasArc = left.HasArc || right.HasArc;
            return new RampPath(segs, baseZ, isVariableWidth: true, forceHasArc: hasArc);
        }

        /// <summary>Reverses a path's segment order and each segment's own direction.</summary>
        private static RampPath Reverse(RampPath p)
        {
            var rev = new List<RampPathSegment>();
            for (int i = p.Segments.Count - 1; i >= 0; i--)
                rev.Add(p.Segments[i].Reversed());
            return new RampPath(rev, p.BaseElevation, p.IsVariableWidth, p.HasArc);
        }

        /// <summary>Checks a selected start/end cap actually touches both edges (within tolerance),
        /// catching a mismatched or wrongly-picked outline early with a clear message.</summary>
        private static void ValidateCap(
            IList<Curve> capCurves, (double X, double Y) a, (double X, double Y) b, string which)
        {
            if (capCurves.Count == 0)
                throw new RampCalcException($"Select the {which} edge line connecting the left and right boundaries.");

            var pts = new List<(double X, double Y)>();
            foreach (Curve c in capCurves)
            {
                pts.Add((FromFeet(c.GetEndPoint(0).X), FromFeet(c.GetEndPoint(0).Y)));
                pts.Add((FromFeet(c.GetEndPoint(1).X), FromFeet(c.GetEndPoint(1).Y)));
            }

            const double capTolerance = 0.3;
            bool nearA = pts.Exists(p => Dist(p.X, p.Y, a.X, a.Y) <= capTolerance);
            bool nearB = pts.Exists(p => Dist(p.X, p.Y, b.X, b.Y) <= capTolerance);
            if (!nearA || !nearB)
                throw new RampCalcException(
                    $"The {which} edge line does not connect the left and right edges (within {capTolerance:F1} m). " +
                    $"Select a line running from the left boundary to the right boundary at the ramp's {which}.");
        }

        private static double Dist(double x0, double y0, double x1, double y1)
        {
            double dx = x1 - x0, dy = y1 - y0;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static RampPathSegment ToSegment(Curve curve)
        {
            if (curve is Arc arc)
            {
                if (Math.Abs(arc.Normal.Z) < 0.999)
                    throw new RampCalcException(
                        "A selected arc is not horizontal. Draw the ramp arcs in a plan view.");
                double r = FromFeet(arc.Radius);
                double cx = FromFeet(arc.Center.X), cy = FromFeet(arc.Center.Y);
                double sx = FromFeet(curve.GetEndPoint(0).X), sy = FromFeet(curve.GetEndPoint(0).Y);
                double a0 = Math.Atan2(sy - cy, sx - cx);
                double sweep = FromFeet(curve.Length) / r;
                // Bounded curves run start -> end with increasing parameter; arcs
                // parameterize CCW around their normal, so normal up = CCW travel.
                int dir = arc.Normal.Z > 0 ? 1 : -1;
                return new RampArcSegment(cx, cy, r, a0, sweep, dir);
            }

            if (curve is Line)
            {
                XYZ p0 = curve.GetEndPoint(0);
                XYZ p1 = curve.GetEndPoint(1);
                return new RampLineSegment(
                    FromFeet(p0.X), FromFeet(p0.Y), FromFeet(p1.X), FromFeet(p1.Y));
            }

            throw new RampCalcException("Only straight model lines and model arcs are supported.");
        }

        private static bool Near((double X, double Y) a, (double X, double Y) b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy <= JoinTolerance * JoinTolerance;
        }

        private static double FromFeet(double feet)
            => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
    }
}
