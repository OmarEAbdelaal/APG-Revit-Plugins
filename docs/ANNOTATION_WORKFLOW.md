# Magic Annotation — user workflow

Magic Annotation annotates the **active view** (floor/ceiling/engineering/area plan,
section, or elevation) in one step. You always choose exactly what to place — the
command never guesses from the view scale.

## Running it

1. Open the view you want annotated (a plan, section or elevation — not a sheet,
   3D or drafting view).
2. **APG Revit Plugins ▸ Magic Annotation ▸ Magic Annotation**.
3. Tick the annotation types you want in the checklist and press **Annotate**.
4. Read the summary: what was created, notes about anything skipped, and a list of
   suggested callouts (never created automatically).

Everything is one transaction: **one Ctrl+Z removes the whole run**. Re-running the
command with *Replace previous Magic Annotation run* ticked (the default) deletes
what the previous run placed in that view before placing anew — manual annotations
are never touched.

## What each option places

| Option | View types | What it does |
|---|---|---|
| Overall dimensions | plan / section / elevation | First↔last grid (plans) or lowest↔highest level (sections/elevations), one step outside the main string |
| Grid dimensions | plan | A dimension string through every straight grid, on the bottom and left of the drawing |
| Detailed opening dimensions | plan | Along each exterior wall (any wall with openings if none is marked Exterior): wall end → door/window centrelines → wall end |
| Level dimensions | section / elevation | Floor-to-floor string on the right of the crop region |
| Room tags | plan | Room centre; rooms already tagged in the view are skipped |
| Door / window tags | plan | Beside the opening, using the project's default loaded tag family for that category |
| Wall tags | all | Wall midpoint, pushed to the exterior side |
| Spot elevations | plan | Lowest and highest walkable surface of every stair and ramp (incl. APG Ramp Creator floors) |
| Ramp slope notes | plan | "UP  S = x.x%" — exact from the Comments of APG-created ramps, estimated (≈) for other ramps |
| Stair path arrows | plan | Revit's native up/down path annotation for stairs that don't have one in the view |
| Suggest callouts | all | Lists stairs, ramps and wet areas (WC, bath, kitchen, lift, shaft…) worth a callout — advisory only |

## Placement rules

- Offsets are **paper millimetres × view scale**, so the layout stays readable at
  1:50 and 1:200 alike (grid string 12 mm outside the grids, overall +8 mm further,
  opening dims 8 mm off the wall face, level string 15 mm right of the crop).
- Tags ask an occupancy map for a free spot before being placed: the map is seeded
  with every dimension, tag, text note and spot dimension already in the view, and
  each new tag reserves its own footprint. Congested tags are nudged in growing
  rings (4/8/12 mm); if a tag must move far from its element it gets a leader.
- Tag graphics come from the **default loaded tag family per category** — if a
  category has no tag family loaded, the summary tells you to load one.

## Notes and limitations (v1)

- Placement defaults follow common drafting practice; they are meant to be
  calibrated against the office reference sheet (annotation types, exact offsets,
  which sides carry the grid strings).
- Callouts are suggested, never created — creating callout views automatically is
  too intrusive.
- Elevation "void/solid" façade dimension strings are not split yet; sections and
  elevations get level strings + overall.
- In-place stairs and stairs inside groups may not accept a path arrow; the summary
  reports them.
