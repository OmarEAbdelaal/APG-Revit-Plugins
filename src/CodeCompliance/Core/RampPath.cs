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
    ///
    /// s is never clamped: values below 0 or beyond the segment length continue the
    /// geometry (straight on, or around the same circle), which is how a ramp is
    /// extended to the exact run the calculation asks for.
    /// </summary>
    public abstract class RampPathSegment
    {
        public abstract bool IsArc { get; }
        public abstract double DrawnLength { get; }
        public abstract (double X, double Y) Start { get; }
        public abstract (double X, double Y) End { get; }
        public abstract RampPathSegment Reversed();

        /// <summary>Total plan sweep angle (radians, unsigned); 0 for straight segments.</summary>
        public virtual double PlanTurn => 0;

        /// <summary>Design-line length of this segment for the given drawn-line location, total width and design offset.</summary>
        public abstract double CenterlineLength(RampLineLocation location, double width, double designOffset = 0);

        /// <summary>
        /// Left/right ramp edge positions at design-line arc-length s from the
        /// segment start. s may fall outside [0, length] — the geometry continues.
        /// </summary>
        public abstract (double LX, double LY, double RX, double RY) EdgesAt(
            double s, RampLineLocation location, double width, double designOffset = 0);

        /// <summary>
        /// The piece of this segment between its own arc-lengths s0 and s1 (drawn
        /// geometry, i.e. location = Center and width = 0). Values outside
        /// [0, DrawnLength] extend the segment rather than clamping.
        /// </summary>
        public abstract RampPathSegment SubSegment(double s0, double s1);

        /// <summary>
        /// One segment covering this and <paramref name="next"/> when the two are
        /// really the same line or the same circle drawn in two pieces; null when
        /// they cannot be joined. Keeps a ramp from being split where it is straight.
        /// </summary>
        public virtual RampPathSegment? TryMergeWith(RampPathSegment next) => null;
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

        public override RampPathSegment SubSegment(double s0, double s1)
            => new RampLineSegment(_x0 + _tx * s0, _y0 + _ty * s0, _x0 + _tx * s1, _y0 + _ty * s1);

        public override RampPathSegment? TryMergeWith(RampPathSegment next)
        {
            if (!(next is RampLineSegment line))
                return null;
            (double ex, double ey) = End;
            (double sx, double sy) = line.Start;
            if ((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy) > 1e-6)   // 1 mm
                return null;
            if (Math.Abs(_tx * line._ty - _ty * line._tx) > 1e-6)       // not parallel
                return null;
            if (_tx * line._tx + _ty * line._ty <= 0)                   // opposite direction
                return null;
            (double nx, double ny) = line.End;
            return new RampLineSegment(_x0, _y0, nx, ny);
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
        public override double PlanTurn => _sweep;
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

        public override RampPathSegment SubSegment(double s0, double s1)
            => new RampArcSegment(_cx, _cy, _r, _a0 + _dir * s0 / _r, Math.Abs(s1 - s0) / _r, _dir);

        public override RampPathSegment? TryMergeWith(RampPathSegment next)
        {
            if (!(next is RampArcSegment arc) || arc._dir != _dir)
                return null;
            if (Math.Abs(arc._r - _r) > 1e-4)
                return null;
            if (Math.Abs(arc._cx - _cx) > 1e-4 || Math.Abs(arc._cy - _cy) > 1e-4)
                return null;
            if (Math.Abs(Wrap(arc._a0 - (_a0 + _dir * _sweep))) > 1e-4)
                return null; // does not continue where this one ends
            return new RampArcSegment(_cx, _cy, _r, _a0, _sweep + arc._sweep, _dir);
        }

        /// <summary>Angle folded into (-pi, pi].</summary>
        private static double Wrap(double angle)
        {
            while (angle > Math.PI) angle -= 2.0 * Math.PI;
            while (angle <= -Math.PI) angle += 2.0 * Math.PI;
            return angle;
        }
    }

    /// <summary>
    /// One piece of a ramp whose outline was drawn explicitly: a left-edge curve and
    /// a right-edge curve that may differ in length, radius and shape, so the ramp
    /// width varies along the run. Each edge keeps its own true geometry — a drawn
    /// arc stays an arc — and stationing runs along the curve traced by the midpoints
    /// between the two edges, so <see cref="EdgesAt"/> ignores the location, width
    /// and design-offset arguments: the drawing already fixes them.
    /// </summary>
    public sealed class RampOutlineSegment : RampPathSegment
    {
        private const int MidSamples = 64;

        private readonly RampPathSegment _left, _right;
        private readonly double _leftLen, _rightLen, _len;

        public RampOutlineSegment(RampPathSegment left, RampPathSegment right)
        {
            _left = left;
            _right = right;
            _leftLen = left.DrawnLength;
            _rightLen = right.DrawnLength;
            _len = MeasureMidLength();
            if (_len < 1e-6)
                _len = 1e-6;
        }

        public RampPathSegment LeftEdge => _left;
        public RampPathSegment RightEdge => _right;

        public override bool IsArc => _left.IsArc || _right.IsArc;
        public override double DrawnLength => _len;
        public override double PlanTurn => Math.Max(_left.PlanTurn, _right.PlanTurn);

        public override (double X, double Y) Start => MidAt(0);
        public override (double X, double Y) End => MidAt(1);

        // Reversing the direction of travel swaps which edge is on the left.
        public override RampPathSegment Reversed()
            => new RampOutlineSegment(_right.Reversed(), _left.Reversed());

        public override double CenterlineLength(RampLineLocation location, double width, double designOffset = 0)
            => _len;

        public override (double LX, double LY, double RX, double RY) EdgesAt(
            double s, RampLineLocation location, double width, double designOffset = 0)
        {
            double t = s / _len;
            (double lx, double ly, _, _) = _left.EdgesAt(t * _leftLen, RampLineLocation.Center, 0);
            (double rx, double ry, _, _) = _right.EdgesAt(t * _rightLen, RampLineLocation.Center, 0);
            return (lx, ly, rx, ry);
        }

        public override RampPathSegment SubSegment(double s0, double s1)
        {
            double t0 = s0 / _len, t1 = s1 / _len;
            return new RampOutlineSegment(
                _left.SubSegment(t0 * _leftLen, t1 * _leftLen),
                _right.SubSegment(t0 * _rightLen, t1 * _rightLen));
        }

        public override RampPathSegment? TryMergeWith(RampPathSegment next)
        {
            if (!(next is RampOutlineSegment other))
                return null;
            RampPathSegment? left = _left.TryMergeWith(other._left);
            RampPathSegment? right = _right.TryMergeWith(other._right);
            return left != null && right != null ? new RampOutlineSegment(left, right) : null;
        }

        /// <summary>Edge-to-edge width at fraction t (0 = start, 1 = end) of the piece.</summary>
        public double WidthAtFraction(double t)
        {
            (double lx, double ly, double rx, double ry) = EdgesAt(t * _len, RampLineLocation.Center, 0);
            return Math.Sqrt((rx - lx) * (rx - lx) + (ry - ly) * (ry - ly));
        }

        /// <summary>
        /// Radius of the inner (tighter) drawn edge, or null when both edges are
        /// straight. A straight edge counts as infinite radius, so a piece with one
        /// straight and one curved edge reports the curved one.
        /// </summary>
        public double? InnerEdgeRadius()
        {
            double? left = (_left as RampArcSegment)?.DrawnRadius;
            double? right = (_right as RampArcSegment)?.DrawnRadius;
            if (!left.HasValue)
                return right;
            if (!right.HasValue)
                return left;
            return Math.Min(left.Value, right.Value);
        }

        /// <summary>Radius of the curve midway between the two edges, when both are arcs.</summary>
        public double? CenterRadius()
        {
            double? left = (_left as RampArcSegment)?.DrawnRadius;
            double? right = (_right as RampArcSegment)?.DrawnRadius;
            return left.HasValue && right.HasValue ? (left.Value + right.Value) / 2.0 : (double?)null;
        }

        private (double X, double Y) MidAt(double t)
        {
            (double lx, double ly, _, _) = _left.EdgesAt(t * _leftLen, RampLineLocation.Center, 0);
            (double rx, double ry, _, _) = _right.EdgesAt(t * _rightLen, RampLineLocation.Center, 0);
            return ((lx + rx) / 2.0, (ly + ry) / 2.0);
        }

        /// <summary>
        /// Length of the midpoint curve, sampled finely enough that the polyline
        /// error stays well under a millimetre even on a quarter-circle piece.
        /// </summary>
        private double MeasureMidLength()
        {
            double total = 0;
            (double X, double Y) previous = MidAt(0);
            for (int i = 1; i <= MidSamples; i++)
            {
                (double X, double Y) current = MidAt((double)i / MidSamples);
                double dx = current.X - previous.X, dy = current.Y - previous.Y;
                total += Math.Sqrt(dx * dx + dy * dy);
                previous = current;
            }
            return total;
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
        private const double MinPieceLength = 0.02; // 2 cm — shorter outline pieces are folded away

        public IReadOnlyList<RampPathSegment> Segments { get; }
        public double BaseElevation { get; }   // meters, from the path start
        public bool HasArc { get; }
        public double DrawnLength { get; }

        /// <summary>True when built from <see cref="FromOutline"/> — left/right edges were
        /// drawn explicitly, so the ramp width may vary along its length.</summary>
        public bool IsVariableWidth { get; }

        private RampPath(List<RampPathSegment> segments, double baseElevation, bool isVariableWidth = false)
        {
            Segments = segments;
            BaseElevation = baseElevation;
            HasArc = segments.Any(s => s.IsArc);
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
        /// Zero for straight ramps, single-lane ramps, and drawn outlines (whose
        /// lanes are already implied by the edges the user drew).
        /// </summary>
        public double DesignOffsetFor(RampLineLocation location, double totalWidth, int lanes)
        {
            if (lanes <= 1 || IsVariableWidth)
                return 0;
            double laneWidth = totalWidth / lanes;
            return InnerSide(location, totalWidth) * (totalWidth - laneWidth) / 2.0;
        }

        /// <summary>
        /// Smallest inner-edge radius along the path, or null when it is all straight.
        /// For a drawn outline this is the tighter of the two drawn edge radii — the
        /// radius the user actually drew, not an estimate.
        /// </summary>
        public double? MinInnerRadius(RampLineLocation location, double width)
        {
            double? min = null;
            foreach (RampPathSegment seg in Segments)
            {
                double? radius = seg is RampArcSegment arc ? arc.InnerRadius(location, width)
                               : (seg as RampOutlineSegment)?.InnerEdgeRadius();
                if (radius.HasValue && (!min.HasValue || radius.Value < min.Value))
                    min = radius;
            }
            return min;
        }

        /// <summary>Narrowest edge-to-edge width along a drawn outline (meters); 0 otherwise.</summary>
        public double MinWidthAlongPath() => WidthRange().Min;

        /// <summary>Widest edge-to-edge width along a drawn outline (meters); 0 otherwise.</summary>
        public double MaxWidthAlongPath() => WidthRange().Max;

        private (double Min, double Max) WidthRange()
        {
            const int samples = 8;
            double min = double.MaxValue, max = 0;
            foreach (RampPathSegment seg in Segments)
            {
                if (!(seg is RampOutlineSegment outline))
                    continue;
                for (int i = 0; i <= samples; i++)
                {
                    double w = outline.WidthAtFraction((double)i / samples);
                    if (w < min) min = w;
                    if (w > max) max = w;
                }
            }
            return min == double.MaxValue ? (0, 0) : (min, max);
        }

        /// <summary>
        /// Design-line radius when the whole path is one curve (the helical case where
        /// the ramp may loop past 360 degrees); null otherwise.
        /// </summary>
        public double? SingleArcDesignRadius(RampLineLocation location, double width, double designOffset = 0)
            => Segments.Count != 1 ? (double?)null
             : (Segments[0] as RampArcSegment)?.DesignRadius(location, width, designOffset)
               ?? (Segments[0] as RampOutlineSegment)?.CenterRadius();

        /// <summary>Centreline radius when the whole path is one curve; null otherwise.</summary>
        public double? SingleArcCenterlineRadius(RampLineLocation location, double width)
            => Segments.Count != 1 ? (double?)null
             : (Segments[0] as RampArcSegment)?.CenterlineRadius(location, width)
               ?? (Segments[0] as RampOutlineSegment)?.CenterRadius();

        /// <summary>
        /// Left/right edges at global design-line arc-length s. Outside the drawn
        /// path the first/last segment continues (straight on, or around its circle),
        /// which is how the ramp reaches exactly the run the calculation asks for.
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
        /// Builds a variable-width ramp path from an explicitly drawn outline: a left-edge
        /// and a right-edge chain (each one or more connected lines/arcs), plus start/end
        /// lines used only to validate that the outline closes up.
        ///
        /// The two edges are paired piece by piece so each piece keeps its drawn geometry:
        /// a straight run stays ONE straight piece and a drawn curve stays a true arc on
        /// both edges, with the two radii free to differ (that is what varies the width).
        /// Edges drawn in several collinear/co-circular pieces are joined first, so the
        /// ramp is never split where it does not change shape.
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

            ValidateCap(startCurves, left.Segments[0].Start, right.Segments[0].Start, "start");
            ValidateCap(endCurves,
                left.Segments[left.Segments.Count - 1].End,
                right.Segments[right.Segments.Count - 1].End, "end");

            List<RampPathSegment> leftPieces = Simplify(left.Segments);
            List<RampPathSegment> rightPieces = Simplify(right.Segments);
            if (leftPieces.Sum(s => s.DrawnLength) < 0.05 || rightPieces.Sum(s => s.DrawnLength) < 0.05)
                throw new RampCalcException("The left or right edge is too short.");

            // Same number of pieces on both edges (the normal case for a ramp drawn
            // straight/curve/straight): pair them up directly, so each piece keeps the
            // exact geometry of both edges. Otherwise fall back to splitting both edges
            // at every shape change on either side.
            List<RampPathSegment> paired = leftPieces.Count == rightPieces.Count
                ? PairByIndex(leftPieces, rightPieces)
                : PairByFraction(leftPieces, rightPieces);

            List<RampPathSegment> segments = Simplify(paired);
            if (segments.Count == 0)
                throw new RampCalcException("The drawn outline is too short to build a ramp from.");

            return new RampPath(
                segments, Math.Min(left.BaseElevation, right.BaseElevation), isVariableWidth: true);
        }

        private static List<RampPathSegment> PairByIndex(
            List<RampPathSegment> left, List<RampPathSegment> right)
        {
            var paired = new List<RampPathSegment>();
            for (int i = 0; i < left.Count; i++)
                paired.Add(new RampOutlineSegment(left[i], right[i]));
            return paired;
        }

        /// <summary>
        /// Fallback pairing when the two edges have a different number of pieces: split
        /// both at every shape change on either edge (by normalized length), so each
        /// resulting piece still spans exactly one line/arc on each edge.
        /// </summary>
        private static List<RampPathSegment> PairByFraction(
            List<RampPathSegment> left, List<RampPathSegment> right)
        {
            double leftLen = left.Sum(s => s.DrawnLength);
            double rightLen = right.Sum(s => s.DrawnLength);

            var fractions = new List<double>();
            fractions.AddRange(Breakpoints(left, leftLen));
            fractions.AddRange(Breakpoints(right, rightLen));
            fractions.Sort();

            // Keep only breakpoints far enough apart to leave a usable piece on both edges.
            double minGap = Math.Max(MinPieceLength / leftLen, MinPieceLength / rightLen);
            var kept = new List<double> { 0.0 };
            foreach (double f in fractions)
                if (f - kept[kept.Count - 1] > minGap && f < 1.0 - minGap)
                    kept.Add(f);
            kept.Add(1.0);

            var paired = new List<RampPathSegment>();
            for (int i = 0; i < kept.Count - 1; i++)
            {
                RampPathSegment? l = SubAt(left, leftLen, kept[i], kept[i + 1]);
                RampPathSegment? r = SubAt(right, rightLen, kept[i], kept[i + 1]);
                if (l != null && r != null)
                    paired.Add(new RampOutlineSegment(l, r));
            }
            return paired;
        }

        /// <summary>Normalized positions of the joints between an edge's pieces.</summary>
        private static IEnumerable<double> Breakpoints(List<RampPathSegment> pieces, double total)
        {
            double offset = 0;
            for (int i = 0; i < pieces.Count - 1; i++)
            {
                offset += pieces[i].DrawnLength;
                yield return offset / total;
            }
        }

        /// <summary>The piece of one edge chain between two normalized positions.</summary>
        private static RampPathSegment? SubAt(
            List<RampPathSegment> pieces, double total, double t0, double t1)
        {
            double s0 = t0 * total, s1 = t1 * total, mid = (s0 + s1) / 2.0;
            double offset = 0;
            foreach (RampPathSegment piece in pieces)
            {
                double len = piece.DrawnLength;
                if (mid <= offset + len || ReferenceEquals(piece, pieces[pieces.Count - 1]))
                    return s1 - s0 < MinPieceLength ? null : piece.SubSegment(s0 - offset, s1 - offset);
                offset += len;
            }
            return null;
        }

        /// <summary>Joins consecutive pieces that are really one line or one circle.</summary>
        private static List<RampPathSegment> Simplify(IReadOnlyList<RampPathSegment> pieces)
        {
            var result = new List<RampPathSegment>();
            foreach (RampPathSegment piece in pieces)
            {
                if (result.Count > 0)
                {
                    RampPathSegment? merged = result[result.Count - 1].TryMergeWith(piece);
                    if (merged != null)
                    {
                        result[result.Count - 1] = merged;
                        continue;
                    }
                }
                result.Add(piece);
            }
            return result;
        }

        /// <summary>Reverses a path's segment order and each segment's own direction.</summary>
        private static RampPath Reverse(RampPath p)
        {
            var rev = new List<RampPathSegment>();
            for (int i = p.Segments.Count - 1; i >= 0; i--)
                rev.Add(p.Segments[i].Reversed());
            return new RampPath(rev, p.BaseElevation, p.IsVariableWidth);
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
