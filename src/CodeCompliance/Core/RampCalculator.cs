using System;
using System.Collections.Generic;

namespace CodeCompliance.Core
{
    /// <summary>Parking ramp plan shape per Dubai Building Code Annex B, Table B.9.</summary>
    public enum RampType
    {
        Straight,
        Curved,
        Helical
    }

    /// <summary>Which side of the ramp band the user's drawn line represents.</summary>
    public enum RampLineLocation
    {
        Left,
        Center,
        Right
    }

    /// <summary>Which of the three key parameters the calculator solves for.</summary>
    public enum RampSolveTarget
    {
        TotalRun,     // R
        FloorHeight,  // h
        Slope         // S
    }

    /// <summary>
    /// Which end of the drawn path is held in place when the ramp is built to the
    /// exact computed run R. The other end moves: the ramp is extended along the
    /// drawn geometry when R is longer than the drawing, or stopped short when it
    /// is shorter, so the built ramp is always exactly R long.
    /// </summary>
    public enum RampEndAnchor
    {
        /// <summary>Start stays where it was drawn; the top end moves.</summary>
        Start,

        /// <summary>End stays where it was drawn; the bottom end moves.</summary>
        End
    }

    /// <summary>Limits for one ramp type from Table B.9. All lengths in meters, slope in percent.</summary>
    public class RampRegulations
    {
        public double MaxSlopePercent { get; set; }
        public double MinLaneWidth { get; set; }
        public double? MinInnerRadius { get; set; }   // null for straight ramps
        public double MinClearance { get; set; }
    }

    /// <summary>
    /// Everything derived from a successful ramp calculation.
    /// Units: meters for lengths, percent for slopes.
    /// </summary>
    public class RampCalcResult
    {
        public RampType Type { get; set; }
        public RampRegulations Regulations { get; set; } = new RampRegulations();

        public double H { get; set; }        // floor-to-floor height
        public double S { get; set; }        // main ramp slope (%)
        public double T { get; set; }        // transition slope = S/2 (%)
        public double X { get; set; }        // transition zone length (each end)
        public double Y { get; set; }        // rise per transition zone
        public double YPrime { get; set; }   // rise over main ramp
        public double XPrime { get; set; }   // horizontal run of main ramp
        public double R { get; set; }        // total horizontal run (centreline)

        public bool SlopeCompliant { get; set; }

        /// <summary>Top-of-slab height above the ramp start at centreline arc-length s (meters).</summary>
        public double HeightAt(double s)
        {
            if (s <= 0)
                return 0;
            if (s <= X)
                return s * T / 100.0;
            if (s <= X + XPrime)
                return Y + (s - X) * S / 100.0;
            if (s <= R)
                return Y + YPrime + (s - X - XPrime) * T / 100.0;
            return H;
        }
    }

    /// <summary>
    /// Pure calculation engine for parking ramps per Dubai Building Code
    /// Annex B, Section B.7.2.2, Tables B.9 / B.10. No Revit API types here;
    /// all lengths are meters and slopes are percentages.
    ///
    /// Formulae:
    ///   X  = interpolate(S)   from Table B.10
    ///   T  = S / 2            transition slope
    ///   Y  = X * S / 200      rise per transition zone
    ///   Y' = h - 2Y           main ramp rise
    ///   X' = 100 * Y' / S     main ramp horizontal run
    ///   R  = X' + 2X = 100h/S + X   total run
    /// </summary>
    public static class RampCalculator
    {
        private static readonly Dictionary<RampType, RampRegulations> TableB9 =
            new Dictionary<RampType, RampRegulations>
            {
                [RampType.Straight] = new RampRegulations { MaxSlopePercent = 12.0, MinLaneWidth = 3.0, MinInnerRadius = null, MinClearance = 2.4 },
                [RampType.Curved] = new RampRegulations { MaxSlopePercent = 12.0, MinLaneWidth = 3.5, MinInnerRadius = 4.0, MinClearance = 2.4 },
                [RampType.Helical] = new RampRegulations { MaxSlopePercent = 8.0, MinLaneWidth = 5.0, MinInnerRadius = 6.0, MinClearance = 2.4 },
            };

        // Table B.10 knot points: (main slope S %, minimum transition length X m)
        private static readonly (double S, double X)[] TableB10 =
        {
            (8.0, 2.4),
            (10.0, 3.0),
            (12.0, 3.6),
        };

        public static RampRegulations GetRegulations(RampType type) => TableB9[type];

        /// <summary>
        /// Minimum transition zone length X (m) for main slope S (%) — linear
        /// interpolation on Table B.10, clamped at both ends.
        /// </summary>
        public static double InterpolateTransitionLength(double s)
        {
            var knots = TableB10;
            if (s <= knots[0].S)
                return knots[0].X;
            if (s >= knots[knots.Length - 1].S)
                return knots[knots.Length - 1].X;
            for (int i = 0; i < knots.Length - 1; i++)
            {
                (double s0, double x0) = knots[i];
                (double s1, double x1) = knots[i + 1];
                if (s0 <= s && s <= s1)
                    return x0 + (s - s0) / (s1 - s0) * (x1 - x0);
            }
            return knots[knots.Length - 1].X;
        }

        /// <summary>
        /// Solve for the unknown parameter given exactly two of {h, S, R}.
        /// Case 1: h + S given, solve R (direct).
        /// Case 2: R + S given, solve h (direct).
        /// Case 3: h + R given, solve S (bisection — X depends on S, no closed form).
        /// Throws <see cref="RampCalcException"/> with a user-readable message when
        /// the inputs are infeasible or violate Table B.9.
        /// </summary>
        public static RampCalcResult Compute(double? h, double? s, double? r, RampType type)
        {
            RampRegulations regs = TableB9[type];
            double maxS = regs.MaxSlopePercent;

            int given = 0;
            if (h.HasValue) given++;
            if (s.HasValue) given++;
            if (r.HasValue) given++;
            if (given != 2)
                throw new RampCalcException("Provide exactly 2 of the 3 parameters: h, S, R.");

            if (h.HasValue && h.Value <= 0) throw new RampCalcException("h must be greater than zero.");
            if (s.HasValue && s.Value <= 0) throw new RampCalcException("S must be greater than zero.");
            if (r.HasValue && r.Value <= 0) throw new RampCalcException("R must be greater than zero.");

            RampCalcResult res;
            if (h.HasValue && s.HasValue)
            {
                // Case 1: h + S -> R
                EnsureSlopeAllowed(s.Value, maxS, type);
                res = Derive(h.Value, s.Value);
            }
            else if (r.HasValue && s.HasValue)
            {
                // Case 2: R + S -> h
                EnsureSlopeAllowed(s.Value, maxS, type);
                double x = InterpolateTransitionLength(s.Value);
                double hComputed = (r.Value - x) * s.Value / 100.0;
                if (hComputed <= 0)
                    throw new RampCalcException(
                        $"Run R = {r.Value:F2} m is shorter than the minimum transition length " +
                        $"X = {x:F2} m at slope {s.Value:F1}%.");
                res = Derive(hComputed, s.Value);
            }
            else
            {
                // Case 3: h + R -> S, bisection on S
                double hv = h!.Value;
                double rv = r!.Value;

                double Residual(double sv) => 100.0 * hv / sv + InterpolateTransitionLength(sv) - rv;

                double sLo = 0.05, sHi = maxS;
                double rLo = Residual(sLo), rHi = Residual(sHi);

                if (rLo * rHi > 0)
                {
                    if (rLo > 0)
                        throw new RampCalcException(
                            $"Run R = {rv:F2} m is too short for h = {hv:F2} m within the allowable " +
                            $"slope range (0-{maxS:F0}%). Minimum run needed = {Residual(maxS) + rv:F2} m.");
                    throw new RampCalcException(
                        $"Run R = {rv:F2} m is too long for h = {hv:F2} m. " +
                        "Try a smaller run or a different ramp type.");
                }

                for (int i = 0; i < 600; i++)
                {
                    double sMid = (sLo + sHi) / 2.0;
                    double rMid = Residual(sMid);
                    if (Math.Abs(rMid) < 1e-7)
                        break;
                    if (rLo * rMid < 0)
                    {
                        sHi = sMid;
                        rHi = rMid;
                    }
                    else
                    {
                        sLo = sMid;
                        rLo = rMid;
                    }
                }

                res = Derive(hv, (sLo + sHi) / 2.0);
            }

            res.Type = type;
            res.Regulations = regs;
            res.SlopeCompliant = res.S <= regs.MaxSlopePercent + 1e-9;
            return res;
        }

        /// <summary>
        /// For a helical ramp that sweeps past a full turn, the vertical gap between
        /// overlapping loops. Returns null when the sweep is less than 360 degrees.
        /// </summary>
        /// <param name="result">A successful calculation.</param>
        /// <param name="centerlineRadius">Actual centreline radius from the drawn arc (m).</param>
        /// <param name="slabThickness">Structural slab thickness (m).</param>
        public static double? MinLoopClearance(RampCalcResult result, double centerlineRadius, double slabThickness)
        {
            double loopLength = 2.0 * Math.PI * centerlineRadius;
            if (result.R <= loopLength)
                return null;

            double min = double.MaxValue;
            const int samples = 400;
            double sMax = result.R - loopLength;
            for (int i = 0; i <= samples; i++)
            {
                double s0 = sMax * i / samples;
                double gap = result.HeightAt(s0 + loopLength) - result.HeightAt(s0) - slabThickness;
                if (gap < min)
                    min = gap;
            }
            return min;
        }

        private static void EnsureSlopeAllowed(double s, double maxS, RampType type)
        {
            if (s > maxS)
                throw new RampCalcException(
                    $"Slope {s:F2}% exceeds the maximum {maxS:F0}% for {type} ramps (Table B.9).");
        }

        private static RampCalcResult Derive(double h, double s)
        {
            double x = InterpolateTransitionLength(s);
            double t = s / 2.0;
            double y = x * s / 200.0;
            double yPrime = h - 2.0 * y;
            if (yPrime < 0)
                throw new RampCalcException(
                    $"Floor height h = {h:F3} m is too small for two transition zones " +
                    $"(each requires Y = {y:F3} m) at slope S = {s:F1}%. " +
                    $"Minimum h needed: {2 * y:F3} m.");
            double xPrime = 100.0 * yPrime / s;
            return new RampCalcResult
            {
                H = h,
                S = s,
                T = t,
                X = x,
                Y = y,
                YPrime = yPrime,
                XPrime = xPrime,
                R = xPrime + 2.0 * x,
            };
        }
    }

    /// <summary>User-readable calculation error (infeasible geometry or code violation).</summary>
    public class RampCalcException : Exception
    {
        public RampCalcException(string message) : base(message)
        {
        }
    }
}
