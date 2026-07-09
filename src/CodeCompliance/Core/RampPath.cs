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
    /// The full ramp path: an ordered chain of line/arc segments in plan.
    /// Built either from model lines the user selected or from clicked points.
    /// </summary>
    public class RampPath
    {
        private const double JoinTolerance = 0.02; // 2 cm — drawn lines should snap together

        public IReadOnlyList<RampPathSegment> Segments { get; }
        public double BaseElevation { get; }   // meters, from the path start
        public bool HasArc { get; }
        public double DrawnLength { get; }

        private RampPath(List<RampPathSegment> segments, double baseElevation)
        {
            Segments = segments;
            BaseElevation = baseElevation;
            HasArc = segments.Any(s => s.IsArc);
            DrawnLength = segments.Sum(s => s.DrawnLength);
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
        /// Zero for straight ramps or single-lane ramps.
        /// </summary>
        public double DesignOffsetFor(RampLineLocation location, double totalWidth, int lanes)
        {
            if (lanes <= 1)
                return 0;
            double laneWidth = totalWidth / lanes;
            return InnerSide(location, totalWidth) * (totalWidth - laneWidth) / 2.0;
        }

        /// <summary>Smallest inner radius among arc segments, or null when the path has no arcs.</summary>
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
            return min;
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
                    "The selected lines do not form one continuous path. " +
                    "Make sure each line starts where the previous one ends.");

            return new RampPath(chain.ToList(), baseZ!.Value);
        }

        /// <summary>Builds a straight-segment path from points the user clicked in order.</summary>
        public static RampPath FromPoints(IList<XYZ> points)
        {
            if (points.Count < 2)
                throw new RampCalcException("Click at least two points to define the ramp path.");

            var segs = new List<RampPathSegment>();
            for (int i = 0; i < points.Count - 1; i++)
            {
                double x0 = FromFeet(points[i].X), y0 = FromFeet(points[i].Y);
                double x1 = FromFeet(points[i + 1].X), y1 = FromFeet(points[i + 1].Y);
                if (Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) < 0.05)
                    continue; // ignore accidental double-clicks
                segs.Add(new RampLineSegment(x0, y0, x1, y1));
            }
            if (segs.Count == 0)
                throw new RampCalcException("The clicked points are too close together.");
            return new RampPath(segs, FromFeet(points[0].Z));
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
