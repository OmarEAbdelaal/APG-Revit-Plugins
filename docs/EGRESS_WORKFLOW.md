# Egress (Escape Route) Analysis Workflow

The plugin analyses escape travel distances using Revit's built-in **Path of Travel**
routing engine: paths go around walls, furniture and other obstacles and pass through
doors, exactly like the native Analyze → Path of Travel tool.

## The 3-step workflow

### Step 1 — Escape Stairs
*APG Revit Plugins → Code Compliance – Fire Fighting → Escape Stairs*

- Detects **all stairs** in the model and lists them (name, type, base level).
- Tick the stairs that serve as escape stairs and press **Save**.
- The choice is stored in a Yes/No instance parameter **`CC_IsEscapeStair`** that the
  plugin binds to the Stairs category automatically. Because it lives in the model you
  can also see/edit it in the stair's Properties palette, tag it, or schedule it.

### Step 2 — Travel Paths
*APG Revit Plugins → Code Compliance – Fire Fighting → Travel Paths* (run in a **floor plan view**)

For every placed room on the plan's level the plugin:

1. Finds the **nearest escape stair** (the egress target for that room).
2. Finds the **most remote point of the room** — the boundary corner farthest from
   that stair, pulled slightly into the room so it sits in free space.
3. Creates a Revit **Path of Travel** from that point to the stair. If the stair's
   plan center can't be reached, it retries targeting the door nearest the stair
   (usually the stair door).

The paths are real Revit elements (category *Lines/Path of Travel*), visible on the
plan and measurable. Re-running the command on the same view deletes the plugin's
previous paths first, so you can iterate freely. Paths created by the plugin are
identified by a Mark starting with `CC - `.

The plugin also ensures the Doors category is in Revit's route-analysis ignored list,
so **paths pass through doors** rather than treating them as walls.

### Step 3 — Egress Report
*APG Revit Plugins → Code Compliance – Fire Fighting → Egress Report*

- Measures the length of every plugin-created travel path (in meters).
- Detects every **door each path passes through** and reads its **Fire Rating**
  (instance or type parameter). Doors with no rating are flagged **"Not rated"**.
- Creates two schedules in the project browser:
  - **CC - Egress Travel Paths** — path lengths and times
  - **CC - Door Fire Ratings** — all doors with their fire rating
- Exports a report to `Documents\CodeCompliance\`:
  - `<model>_Egress_<timestamp>.html` — formatted, printable report with the longest
    travel distance highlighted and unrated doors flagged in red
  - `<model>_Egress_<timestamp>.csv` — same data for Excel

## Current assumptions & limitations (v0.2)

These are deliberate simplifications for the first version — tell me which to refine:

- **Most remote point** is approximated by the room boundary corner farthest
  (straight-line) from the stair. A stricter approach (sampling a grid inside the room
  and maximising *path* distance) is possible but slower.
- **Nearest stair** is chosen by straight-line distance; rooms are not yet checked
  against *two* independent escape routes (common code requirement).
- Analysis is **per level** — run Travel Paths once on each floor plan. Vertical
  travel inside the stair is not added to the distance.
- **Doors crossed** are detected by proximity of the door to the path line (1 m
  tolerance), which is reliable for normal door sizes.
- No pass/fail limit is applied yet — the report states distances; the allowed
  maximum (e.g. NFPA 101 / local code values, sprinklered vs not) will be added
  with the detailed rule set.
