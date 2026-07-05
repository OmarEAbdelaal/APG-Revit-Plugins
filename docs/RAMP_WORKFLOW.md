# Parking Ramp Workflow

Creates a code-compliant parking ramp per **Dubai Building Code, Annex B,
Section B.7.2.2, Tables B.9 / B.10** — the same engine as the standalone
Parking Ramp Calculator app. The ramp is built from native **Floor elements**
shaped with slab-shape ("Modify Sub Elements") points, so the result schedules,
tags and hosts like any other floor.

## User workflow

1. Click **Parking Ramp** on the *APG Plugins → Parking* panel and choose how to
   define the path:
   - **Select drawn model lines** — pick one or more model lines/arcs forming
     the path (they are chained automatically; pick the line at the ramp start
     first — its drawn direction sets the direction of travel, going up), or
   - **Draw the path now** — click points in the view for a straight-segment
     path; press Esc to finish.
2. In the dialog:
   - Choose what the path represents: **left edge**, **centerline** or
     **right edge** of the ramp band (left/right relative to travel direction).
   - Choose the ramp type, number of lanes (1–3), lane width and **floor type**
     (the floor type's thickness is used for the helical loop clearance check).
   - Choose the **target parameter to solve for** — total run **R**, floor
     height **h** or slope **S** — and enter the other two. When R is a known
     input it is prefilled from the drawn path length.
   - Click **Calculate + check compliance**. Results (transition zones X/T/Y,
     main run X'/Y', arc radii and sweep) and all Table B.9 checks are shown
     live. **Create ramp floors** stays disabled until the design is compliant.
3. The ramp — entry transition + main run + exit transition — is created as one
   floor per path segment (arcs are split into chunks of max 170° sweep; helical
   ramps may loop past 360° by extending the last arc). Boundary vertices and
   interior points are raised with the slab shape editor to match the profile,
   so the floors can be edited afterwards with **Modify Sub Elements**.

## Code checks at the input step (Table B.9)

| Check | Straight | Curved | Helical |
|---|---|---|---|
| Max slope | 12 % | 12 % | 8 % |
| Min lane width | 3.0 m | 3.5 m | 5.0 m |
| Min inner radius | — | 4.0 m (smallest arc in the actual drawn path + alignment) | 6.0 m |
| Min clearance 2.4 m | advisory | advisory | checked between overlapping loops (single-arc paths) |

Transition zones follow Table B.10 (X interpolated from S; T = S/2).

## Calculation cases

- **h + S → R** and **R + S → h**: direct formulae.
- **h + R → S**: bisection (X depends on S, no closed form).

The full design data is written to each floor's **Comments** parameter and the
floors are marked `CC - Ramp i/n`. The floors sit on the nearest level at or
below the drawn path's elevation, with the height offset making up the difference.
