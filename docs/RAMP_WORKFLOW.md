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
     lines/arcs. The two edges are paired piece by piece, so each keeps the
     geometry you drew: a straight run stays **one straight piece** and a drawn
     curve stays a **true arc on both edges**, with the two radii free to differ
     — that difference is what varies the width. Edges drawn in several
     collinear or co-circular pieces are joined first, so the ramp is never
     split where its shape doesn't change. The start/end edges aren't used for
     geometry — they just confirm the outline closes up, and a mismatch throws a
     clear error before anything is built.
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
   - Choose the **fixed end** — **start** or **end** of the sketch. The ramp is
     always built to exactly the computed run **R**: the fixed end stays where
     you drew it and the other end moves, extending along the drawn geometry
     (straight on, or around the same curve) when R is longer than the sketch,
     or stopping short when it is shorter. So the sketch sets the alignment and
     the calculation sets the length — the drawing never has to be redrawn to
     match R.
   - Choose the **target parameter to solve for** — total run **R**, floor
     height **h** or slope **S** — and enter the other two. When R is a known
     input it is prefilled from the drawn path length (its centerline, for an
     outline path).
   - Click **Calculate + check compliance**. Results (transition zones X/T/Y,
     main run X'/Y', arc radii and sweep) and all Table B.9 checks are shown
     live; the length row says which end moves and by how much. **Create ramp
     floors** stays disabled until the design is compliant.
3. The ramp — entry transition + main run + exit transition — is created as ONE
   continuous floor slab, following the drawn geometry exactly (straight edges
   stay straight, curved edges stay true arcs, subdivided into ~30° boundary
   pieces on the same circle). Only a path that turns past ~170° is split into
   several floors, since a Revit sketch cannot overlap itself. Boundary vertices
   are raised with the slab shape editor to match the profile, so the floors can
   be edited afterwards with **Modify Sub Elements**.

## Code checks at the input step (Table B.9)

| Check | Straight | Curved | Helical |
|---|---|---|---|
| Max slope | 12 % | 12 % | 8 % |
| Min lane width | 3.0 m | 3.5 m | 5.0 m |
| Min inner radius | — | 4.0 m (smallest arc in the actual drawn path + alignment) | 6.0 m |
| Min clearance 2.4 m | advisory | advisory | checked between overlapping loops (single-arc paths) |

For an outline path the inner radius is read straight off the drawing: it is the
radius of the tighter of the two drawn edge arcs at each curve, so a radius drawn
to the code minimum checks out as compliant.

Transition zones follow Table B.10 (X interpolated from S; T = S/2).

## Calculation cases

- **h + S → R** and **R + S → h**: direct formulae.
- **h + R → S**: bisection (X depends on S, no closed form).

The full design data is written to each floor's **Comments** parameter and the
floors are marked `CC - Ramp i/n`. The floors sit on the nearest level at or
below the drawn path's elevation, with the height offset making up the difference.
