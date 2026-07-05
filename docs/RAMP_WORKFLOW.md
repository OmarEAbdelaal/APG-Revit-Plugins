# Parking Ramp Workflow

Creates a code-compliant parking ramp per **Dubai Building Code, Annex B,
Section B.7.2.2, Tables B.9 / B.10** — the same engine as the standalone
Parking Ramp Calculator app.

## User workflow

1. **Draw a line** in a plan view at the ramp start level, in the direction of
   travel going **up** the ramp:
   - a straight **model line** → Straight ramp
   - a **model arc** → Curved or Helical ramp
2. Click **Parking Ramp** on the *Code Compliance → Parking* panel and select
   the line.
3. In the dialog:
   - Choose what the drawn line represents: **left edge**, **centerline** or
     **right edge** of the ramp band (left/right relative to travel direction).
   - Choose the ramp type, number of lanes (1–3), lane width and slab thickness.
   - Choose the **target parameter to solve for** — total run **R**, floor
     height **h** or slope **S** — and enter the other two. When R is a known
     input it is prefilled from the drawn line length.
   - Click **Calculate + check compliance**. Results (transition zones X/T/Y,
     main run X'/Y', arc radii and sweep) and all Table B.9 checks are shown
     live. **Create ramp** stays disabled until the design is compliant.
4. The ramp — entry transition + main run + exit transition — is created as a
   **DirectShape** in the *Ramps* category, starting at the line's start point
   and following its direction for the computed run R (helical ramps may sweep
   past 360°).

## Code checks at the input step (Table B.9)

| Check | Straight | Curved | Helical |
|---|---|---|---|
| Max slope | 12 % | 12 % | 8 % |
| Min lane width | 3.0 m | 3.5 m | 5.0 m |
| Min inner radius | — | 4.0 m (from the actual drawn arc + alignment) | 6.0 m |
| Min clearance 2.4 m | advisory | advisory | checked between overlapping loops |

Transition zones follow Table B.10 (X interpolated from S; T = S/2).

## Calculation cases

- **h + S → R** and **R + S → h**: direct formulae.
- **h + R → S**: bisection (X depends on S, no closed form).

The full design data is written to the element's **Comments** parameter and the
element is marked `CC - Ramp`.
