# Parking Ramp Workflow

Creates a code-compliant parking ramp per **Dubai Building Code, Annex B,
Section B.7.2.2, Tables B.9 / B.10** — the same engine as the standalone
Parking Ramp Calculator app. The ramp is built from native **Floor elements**
shaped with slab-shape ("Modify Sub Elements") points, so the result schedules,
tags and hosts like any other floor.

The floor sketch follows the drawn geometry exactly: straight parts are straight
edges and curved parts are **true arcs with the ramp's real radii** (long curves
are split into several arc pieces, each still on the exact circle). Mixed paths
(straight / curve / straight / curve / ...) build as **one continuous slab**.
For **multi-lane curved ramps**, the slope and total run are measured along the
**centreline of the innermost lane** (nearest the inner curve) — the
code-governing line — not the overall ramp centre.

## User workflow

1. Click **Parking Ramp** on the *APG Revit Plugins → Ramp Creator* panel and choose how to
   define the path. However it's provided, **the ramp starts at the start point of
   the first line/curve you draw or pick** — draw or pick it in the direction of
   travel, going up the ramp.
   - **Select drawn model lines** — pick one or more connected model lines/arcs
     forming a single path (they are chained automatically, in any order, as long
     as consecutive lines/arcs actually touch end to end — straight segments,
     arcs, or any mix of both chain into one continuous ramp sketch).
   - **Draw new lines/arcs now** — opens Revit's own Line tool (Line and Arc,
     with Chain), for full native sketching with snapping and typed dimensions.
     Since a posted Revit command can't hand results back to the same plugin
     call, finish the sketch (Modify or Esc) and run **Parking Ramp** again,
     choosing **Select drawn model lines** (or the outline option below) to
     continue with what you drew.
   - **Select ramp outline (varying width)** — for a ramp that isn't a constant
     width along its run: pick the **left edge**, **right edge**, **start edge**
     and **end edge** separately, each its own chain of one or more connected
     lines/arcs. The left/right edges may differ in length or shape (e.g. an
     outer curve longer than the inner one); the tool resamples both at equal
     normalized arc-length fractions to build the ramp's actual width at every
     station, so a taper or an uneven bend is captured exactly as drawn. The
     start/end edges aren't used for geometry — they just confirm the outline
     closes up, and a mismatch throws a clear error before anything is built.
2. In the dialog:
   - Choose what the path represents: **left edge**, **centerline** or
     **right edge** of the ramp band (left/right relative to travel direction).
     Not shown for an outline path — the left/right edges were drawn explicitly,
     so this choice doesn't apply.
   - Choose the ramp type, number of lanes (1–3), lane width and **floor type**
     (the floor type's thickness is used for the helical loop clearance check).
     For an outline path, lanes/lane width are read-only, pre-filled with the
     narrowest width found along the drawn outline (used for the Table B.9
     width check and, unless overridden, the geometry).
   - Choose the **target parameter to solve for** — total run **R**, floor
     height **h** or slope **S** — and enter the other two. When R is a known
     input it is prefilled from the drawn path length (its centerline, for an
     outline path).
   - Click **Calculate + check compliance**. Results (transition zones X/T/Y,
     main run X'/Y', arc radii and sweep) and all Table B.9 checks are shown
     live. **Create ramp floors** stays disabled until the design is compliant.
3. The ramp — entry transition + main run + exit transition — is created as one
   floor per path segment (arcs are split into chunks of max 170° sweep; helical
   ramps may loop past 360° by extending the last arc; an outline path is split
   into many short, straight-edged floors — roughly every 0.75 m of drawn length
   — so a taper or bend is followed closely). Boundary vertices and interior
   points are raised with the slab shape editor to match the profile, so the
   floors can be edited afterwards with **Modify Sub Elements**.

## Code checks at the input step (Table B.9)

| Check | Straight | Curved | Helical |
|---|---|---|---|
| Max slope | 12 % | 12 % | 8 % |
| Min lane width | 3.0 m | 3.5 m | 5.0 m |
| Min inner radius | — | 4.0 m (smallest arc in the actual drawn path + alignment) | 6.0 m |
| Min clearance 2.4 m | advisory | advisory | checked between overlapping loops (single-arc paths) |

For an outline path, the inner radius check falls back to a discrete curvature
estimate (a circle fit through each three consecutive centerline stations, minus
the local half-width) since the drawn edges are stored as many small straight
chords rather than true arcs.

Transition zones follow Table B.10 (X interpolated from S; T = S/2).

## Calculation cases

- **h + S → R** and **R + S → h**: direct formulae.
- **h + R → S**: bisection (X depends on S, no closed form).

The full design data is written to each floor's **Comments** parameter and the
floors are marked `CC - Ramp i/n`. The floors sit on the nearest level at or
below the drawn path's elevation, with the height offset making up the difference.
