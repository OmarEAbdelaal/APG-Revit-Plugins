using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core
{
    /// <summary>
    /// What the user's drawn model line tells us about the ramp path.
    /// Lengths in meters (converted from Revit internal feet).
    /// </summary>
    public class RampPathInfo
    {
        public bool IsArc { get; set; }
        public double DrawnLength { get; set; }        // length of the drawn curve
        public double BaseElevation { get; set; }      // Z of the curve start (meters)

        // Arc-only data
        public double DrawnRadius { get; set; }
        public bool CenterIsLeftOfTravel { get; set; } // true when the arc bends left (CCW in plan)

        /// <summary>Centreline radius after applying the drawn-line location and ramp width.</summary>
        public double CenterlineRadius(RampLineLocation location, double widthM)
        {
            double half = widthM / 2.0;
            // Moving toward the arc centre reduces the radius. The drawn line is the
            // left edge, the centreline, or the right edge of the ramp band.
            double shiftTowardCenter = location switch
            {
                RampLineLocation.Center => 0,
                // drawn = left edge: if centre is left, band lies right of the line (away from centre)
                RampLineLocation.Left => CenterIsLeftOfTravel ? -half : half,
                // drawn = right edge: mirror
                _ => CenterIsLeftOfTravel ? half : -half,
            };
            return DrawnRadius - shiftTowardCenter;
        }

        public double InnerRadius(RampLineLocation location, double widthM)
            => CenterlineRadius(location, widthM) - widthM / 2.0;

        public double OuterRadius(RampLineLocation location, double widthM)
            => CenterlineRadius(location, widthM) + widthM / 2.0;

        /// <summary>Length of the ramp centreline implied by the drawn curve.</summary>
        public double CenterlineLength(RampLineLocation location, double widthM)
        {
            if (!IsArc)
                return DrawnLength;
            double rc = CenterlineRadius(location, widthM);
            return DrawnLength * rc / DrawnRadius;
        }
    }

    /// <summary>
    /// Builds the ramp solid (with entry transition, main run and exit transition)
    /// as tessellated geometry for a DirectShape, from the user's drawn line/arc
    /// and a successful <see cref="RampCalcResult"/>.
    /// </summary>
    public static class RampGeometryBuilder
    {
        private const double MaxArcFacetAngle = Math.PI / 60.0; // 3 degrees per facet

        /// <summary>Reads plan-shape data from the drawn curve. Throws <see cref="RampCalcException"/> for unusable curves.</summary>
        public static RampPathInfo AnalyzePath(Curve curve)
        {
            double toM = FromFeet(1.0);
            XYZ start = curve.GetEndPoint(0);

            if (curve is Arc arc)
            {
                if (Math.Abs(arc.Normal.Z) < 0.999)
                    throw new RampCalcException(
                        "The selected arc is not horizontal. Draw the ramp arc in a plan view.");
                return new RampPathInfo
                {
                    IsArc = true,
                    DrawnLength = curve.Length * toM,
                    BaseElevation = start.Z * toM,
                    DrawnRadius = arc.Radius * toM,
                    // Bounded curves run from endpoint 0 to endpoint 1 with increasing
                    // parameter; an arc parameterizes CCW around its normal, so with the
                    // normal pointing up the centre sits on the left of the travel direction.
                    CenterIsLeftOfTravel = arc.Normal.Z > 0,
                };
            }

            if (curve is Line line)
            {
                XYZ end = curve.GetEndPoint(1);
                double dx = (end.X - start.X) * toM;
                double dy = (end.Y - start.Y) * toM;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.01)
                    throw new RampCalcException("The selected line is too short (vertical or zero-length in plan).");
                return new RampPathInfo
                {
                    IsArc = false,
                    DrawnLength = len,
                    BaseElevation = start.Z * toM,
                };
            }

            throw new RampCalcException("Select a straight model line or a model arc.");
        }

        /// <summary>
        /// Builds the ramp geometry. The ramp starts at the drawn curve's start point and
        /// follows its direction for the computed total run R (which may be longer or
        /// shorter than the drawn curve, and may wrap past 360 degrees for helical ramps).
        /// </summary>
        public static IList<GeometryObject> Build(
            Curve drawnCurve,
            RampCalcResult calc,
            double totalWidthM,
            double slabThicknessM,
            RampLineLocation location)
        {
            RampPathInfo info = AnalyzePath(drawnCurve);
            double w = totalWidthM;
            double half = w / 2.0;
            double z0 = info.BaseElevation;
            double toM = FromFeet(1.0);

            // Left/right edge positions (meters, plan XY) at centreline arc-length s.
            Func<double, (double lx, double ly, double rx, double ry)> edges;

            if (!info.IsArc)
            {
                XYZ s0 = drawnCurve.GetEndPoint(0);
                XYZ s1 = drawnCurve.GetEndPoint(1);
                double dx = (s1.X - s0.X) * toM, dy = (s1.Y - s0.Y) * toM;
                double len = Math.Sqrt(dx * dx + dy * dy);
                double tx = dx / len, ty = dy / len;          // travel direction
                double lxv = -ty, lyv = tx;                    // unit left vector
                double px0 = s0.X * toM, py0 = s0.Y * toM;

                // Shift the drawn line to the centreline per the chosen location.
                double shift = location switch
                {
                    RampLineLocation.Left => -half,   // drawn = left edge, centre is to the right
                    RampLineLocation.Right => half,   // drawn = right edge, centre is to the left
                    _ => 0,
                };
                double cx0 = px0 + lxv * shift, cy0 = py0 + lyv * shift;

                edges = s =>
                {
                    double cx = cx0 + tx * s, cy = cy0 + ty * s;
                    return (cx + lxv * half, cy + lyv * half, cx - lxv * half, cy - lyv * half);
                };
            }
            else
            {
                var arc = (Arc)drawnCurve;
                double ccx = arc.Center.X * toM, ccy = arc.Center.Y * toM;
                double rc = info.CenterlineRadius(location, w);
                double rInner = rc - half;
                if (rInner <= 0.01)
                    throw new RampCalcException(
                        "The ramp width does not fit inside the drawn arc " +
                        $"(inner radius would be {rInner:F2} m).");

                XYZ startPt = drawnCurve.GetEndPoint(0);
                double a0 = Math.Atan2(startPt.Y * toM - ccy, startPt.X * toM - ccx);
                double dir = info.CenterIsLeftOfTravel ? 1.0 : -1.0;
                // Left edge is the smaller radius when the centre is on the left.
                double rLeft = info.CenterIsLeftOfTravel ? rc - half : rc + half;
                double rRight = info.CenterIsLeftOfTravel ? rc + half : rc - half;

                edges = s =>
                {
                    double a = a0 + dir * s / rc;
                    double ca = Math.Cos(a), sa = Math.Sin(a);
                    return (ccx + rLeft * ca, ccy + rLeft * sa, ccx + rRight * ca, ccy + rRight * sa);
                };
            }

            List<double> stations = BuildStations(calc, info, location, w);

            // Vertex grids along the path: [station] -> top-left, top-right, bottom-left, bottom-right
            int n = stations.Count;
            var tl = new XYZ[n];
            var tr = new XYZ[n];
            var bl = new XYZ[n];
            var br = new XYZ[n];
            for (int i = 0; i < n; i++)
            {
                double s = stations[i];
                (double lx, double ly, double rx, double ry) = edges(s);
                double zTop = z0 + calc.HeightAt(s);
                double zBot = zTop - slabThicknessM;
                tl[i] = ToFeet(lx, ly, zTop);
                tr[i] = ToFeet(rx, ry, zTop);
                bl[i] = ToFeet(lx, ly, zBot);
                br[i] = ToFeet(rx, ry, zBot);
            }

            var builder = new TessellatedShapeBuilder();
            builder.OpenConnectedFaceSet(true);

            for (int i = 0; i < n - 1; i++)
            {
                int j = i + 1;
                // Top (normal up) and bottom (normal down)
                AddQuad(builder, tl[i], tr[i], tr[j], tl[j]);
                AddQuad(builder, bl[i], bl[j], br[j], br[i]);
                // Left side (outward left) and right side (outward right)
                AddQuad(builder, tl[i], tl[j], bl[j], bl[i]);
                AddQuad(builder, tr[i], br[i], br[j], tr[j]);
            }
            // Start cap (outward against travel) and end cap (outward along travel)
            AddQuad(builder, tl[0], bl[0], br[0], tr[0]);
            AddQuad(builder, tl[n - 1], tr[n - 1], br[n - 1], bl[n - 1]);

            builder.CloseConnectedFaceSet();

            try
            {
                builder.Target = TessellatedShapeBuilderTarget.Solid;
                builder.Fallback = TessellatedShapeBuilderFallback.Salvage;
                builder.Build();
            }
            catch
            {
                // Winding or tolerance problems: accept any geometry rather than fail.
                builder.Clear();
                builder.OpenConnectedFaceSet(false);
                for (int i = 0; i < n - 1; i++)
                {
                    int j = i + 1;
                    AddQuad(builder, tl[i], tr[i], tr[j], tl[j]);
                    AddQuad(builder, bl[i], bl[j], br[j], br[i]);
                    AddQuad(builder, tl[i], tl[j], bl[j], bl[i]);
                    AddQuad(builder, tr[i], br[i], br[j], tr[j]);
                }
                AddQuad(builder, tl[0], bl[0], br[0], tr[0]);
                AddQuad(builder, tl[n - 1], tr[n - 1], br[n - 1], bl[n - 1]);
                builder.CloseConnectedFaceSet();
                builder.Target = TessellatedShapeBuilderTarget.AnyGeometry;
                builder.Fallback = TessellatedShapeBuilderFallback.Mesh;
                builder.Build();
            }

            return builder.GetBuildResult().GetGeometricalObjects();
        }

        /// <summary>Zone boundaries plus arc facet subdivisions, 0 .. R.</summary>
        private static List<double> BuildStations(
            RampCalcResult calc, RampPathInfo info, RampLineLocation location, double widthM)
        {
            double[] breaks = { 0, calc.X, calc.X + calc.XPrime, calc.R };
            var stations = new List<double>();

            double maxStep = double.MaxValue;
            if (info.IsArc)
            {
                double rc = info.CenterlineRadius(location, widthM);
                maxStep = Math.Max(0.05, rc * MaxArcFacetAngle);
            }

            for (int k = 0; k < breaks.Length - 1; k++)
            {
                double a = breaks[k], b = breaks[k + 1];
                double span = b - a;
                if (span <= 1e-9)
                    continue;
                int steps = Math.Max(1, (int)Math.Ceiling(span / maxStep));
                for (int i = 0; i < steps; i++)
                    stations.Add(a + span * i / steps);
            }
            stations.Add(calc.R);
            return stations;
        }

        // Quads are emitted as two triangles: faces stay planar even on warped
        // (helical) surfaces, which TessellatedShapeBuilder requires.
        private static void AddQuad(TessellatedShapeBuilder builder, XYZ a, XYZ b, XYZ c, XYZ d)
        {
            AddTriangle(builder, a, b, c);
            AddTriangle(builder, a, c, d);
        }

        private static void AddTriangle(TessellatedShapeBuilder builder, XYZ a, XYZ b, XYZ c)
        {
            if (a.DistanceTo(b) < 1e-6 || b.DistanceTo(c) < 1e-6 || c.DistanceTo(a) < 1e-6)
                return;
            builder.AddFace(new TessellatedFace(new List<XYZ> { a, b, c }, ElementId.InvalidElementId));
        }

        private static XYZ ToFeet(double xm, double ym, double zm)
        {
            return new XYZ(
                UnitUtils.ConvertToInternalUnits(xm, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(ym, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(zm, UnitTypeId.Meters));
        }

        private static double FromFeet(double feet)
            => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
    }
}
