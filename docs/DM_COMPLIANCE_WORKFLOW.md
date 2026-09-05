# DM BIM Compliance — Dubai Municipality e-submission check

The **DM BIM Compliance** plugin reviews an open Revit model against Dubai Municipality's
BIM e-submission requirements *before* the IFC is exported, lists every element that has to
be modified together with the type of modification, frames those elements in a 3D section
box, and hands you the prompt that lets Claude apply the fix through the Revit MCP
connection.

```
APG Revit Plugins (tab)
└── DM BIM Compliance (panel)
    ├── DM Compliance – dashboard: run the audit, filter findings, highlight elements,
    │                    copy the Claude fix prompt, export the report
    └── DM Report     – run the audit silently and write the report files
```

---

## 1. What it checks

The rules are not written into the C# code. They are the Dubai Municipality data set itself,
shipped with the plugin and reloadable when DM revises the standard (see §5):

| Data | Used for |
|---|---|
| `ids_rules.json` — DM's own 127 IDS rules | The rulebook DM's automated checker runs |
| `attr_*.csv` — Appendix B element attribute matrices | Which attribute each element class needs, per permit stage |
| `usage_*.csv` — Appendix C controlled vocabularies | Valid space, unit, zone and building occupancy codes |
| `category_to_ifc.json` | Which IFC class a Revit category exports as |
| `shared_parameters.json`, `property_sets.json` | The DM shared parameters and the "Building Permit" property set |
| `modelling_practices.json` — DM's 16 recommended modelling practices | How the model itself has to be built (see phase 7) |

The audit runs in eight phases, mirroring DM's own offline self-assessment:

1. **Project / site / building** — Revit project information, the DM attributes on
   IfcProject, IfcSite and IfcBuilding (ParcelId, BIMStandardVersion, GateLevel, BuildingNum,
   the four area attributes …), and whether the DM shared parameters are bound at all.
2. **Levels** — `ABBREVIATION_IDENTIFICATION` uppercase naming, duplicate names, the
   mandatory gate level, storeys without a plan view, reference levels still flagged as
   building storeys, and the four storey area attributes.
3. **Rooms, spaces and units** — unplaced and unenclosed rooms, room numbering and
   duplicates, every Appendix B IfcSpace attribute, the parking attributes, the Appendix C
   usage codes (invalid codes are reported literally), description/code mismatches, and the
   ±5 % area reconciliation between the placed rooms and the declared storey areas.
4. **Element attributes** — for walls, doors, windows, slabs, roofs, ceilings, curtain wall
   panels and mullions, columns, beams, foundations, railings, stairs and flights, ramps,
   furniture, elevators and generic models: every Appendix B attribute required for the
   selected permit stage plus DM's IDS property rules, split into *not bound to the category*
   (the shared parameter is missing) and *bound but empty* (a value is missing). Categories
   that can export as several IFC classes are also checked for an explicit `IfcExportAs`.
5. **Object naming** — the type names actually used in the model against
   `Category_FunctionalType_Discipline_Description`: field separator, spaces, forbidden
   characters, 30-character limit.
6. **Geo-referencing and units** — survey point still at the origin, site location outside
   Dubai, `GateLevel` against the elevation of the gate level, non-metric project units.
7. **Recommended modelling practices** — DM's *Recommended Modelling Practices*, checked
   against the geometry of the model itself (see §1a). Switch the phase off with
   *Check modelling practices* when you only want the data checks: it is the slowest phase.
8. **Export readiness** — file naming convention and the ParcelId cross-check, imported CAD,
   CAD and Revit links, in-place families, the model warning count, and the IFC export setup
   checklist.

Findings are classified the way DM's own QA/QC checker reports them: **Critical** (blocks the
submission), **Error** (fix before submitting) and **Warning** (fix where feasible).

The audit is **read-only**. Nothing in the model changes until you decide to fix something.

### 1a. The recommended modelling practices (phase 7)

Phase 7 checks DM's *Recommended Modelling Practices* — the way the model has to be built so
the exported IFC survives DM's platform. Each practice carries its own id, so a finding can be
traced back to the practice (and searched for by typing e.g. `RMP-09` in the search box):

| Id | Practice | What is reported |
|---|---|---|
| RMP-01 | Walls run from FFL down to SSL and up to the underside of the slab | Walls starting above their level, walls whose top reaches into the slab above, walls with an unconnected height |
| RMP-02 | One column per storey | Columns constrained across more than one storey (foundation to roof) |
| RMP-16 | One **wall** per storey | see below — the same rule for walls |
| RMP-03 | Spaces properly enclosed | Walls and columns that are not room bounding, so the boundary runs through them |
| RMP-04 | No empty area is left | Storeys whose placed rooms cover less than 90 % of the built floor area |
| RMP-05 | Gate level and road level elements | Hardscape, landscape and boundary walls not on the gate level; roads not on the road level |
| RMP-06 | Elements on the level they sit on | Elements whose geometry is entirely outside the storey of their level parameter (ramps and railings left on the gate level, elements floating above the building) |
| RMP-07 | FFL / SSL level pairs | Finished floor levels with no matching SSL reference level |
| RMP-08 | Modelled with the correct tool | A pergola, canopy or louvre screen assembled from slabs and columns instead of one generic model |
| RMP-09 | Finishes export as IfcCovering | Floor finishes that would export as IfcSlab and cladding that would export as IfcWall |
| RMP-10 | Space height adjusted to the ceiling | Rooms whose height stops below or runs past the ceiling of that room |
| RMP-11 | No clash with the linked model | Elements overlapping the volume of an element in a linked model |
| RMP-12 | One room per enclosed region | Rooms Revit reports in the same enclosed region, or not enclosed at all |
| RMP-13 | Clear unwanted elements | Furniture, casework, fittings and structural content still exported from the architectural model |
| RMP-14 | Dummy level for split levels | Groups of walls and rooms sitting at another elevation on a storey level |
| RMP-15 | Purge before the export | Unused loadable family types the export would carry |
| RMP-16 | One wall per storey | Walls drawn through several floors instead of one segment per storey (the column rule of RMP-02, applied to walls) |

The wording, the severity, the type of modification and **every tolerance** of these practices
live in `modelling_practices.json`. Set `"enabled": false` on a practice to switch it off,
change `"severity"`, or retune a threshold — no new plugin build (see §6). Each practice also
carries its Revit steps and an MCP hint, both of which travel into the fix prompt.

---

## 2. The dashboard

Click **DM Compliance**. The dashboard opens **modeless**: Revit stays fully usable next to
it — pan, zoom, edit, open another view — and the dashboard never has to be closed to work on
a finding. Clicking the ribbon button again brings the open dashboard forward instead of
opening a second one. Everything the dashboard asks Revit to do (the audit, the selection,
the 3D highlight, the parameter binding) is executed by Revit itself through an external
event, so it always runs in a valid API context.

The audit runs immediately and the dashboard shows:

- **Five tiles** — critical, errors, warnings, how many elements need modification, and a
  submission-readiness percentage.
- **Permit stage** — Final permit (default) or Preliminary permit. DM requires different
  attributes at each stage, and the Appendix B "Required" column is read accordingly.
  *Include conditional attributes* adds the ones DM marks Conditional (unit data and similar).
- **A findings table** — severity, phase, scope, the issue, the **type of modification**
  (set parameter, load/bind parameter, rename, model change, project setup, review), the DM
  attribute involved and how many of the checked elements are affected. Filter by severity,
  by phase, by type of modification or by free text (the practice ids are searchable too).
- **An element list** for the selected finding — element id, category, name and level, one
  row per element. Pick a row (or several) to work on those elements alone: **Select in
  Revit** selects them, **Highlight selected element** frames exactly them in the 3D
  compliance view, and a double-click does the same. With *Highlight in 3D on select* ticked
  the highlight follows every pick, so you can walk down the list and watch each element in
  the model. Revit stays open and usable the whole time.
- **A detail panel** for the selected finding — what is wrong, the DM reference, what to
  change, and the ready-made **prompt for Claude**.

### Everything is remembered

Permit stage, the three audit switches, all four filters, the two working preferences and the
window size and position are written to
`%LOCALAPPDATA%\APGRevitPlugins\DmCompliance\dm-ui-settings.json` when the dashboard closes
(and before every audit run) and restored the next time it opens — so re-opening the dashboard
picks the work up exactly where you left it. **DM Report** uses the same saved options, so the
silent report matches what the dashboard last showed.

### Buttons

| Button | What it does |
|---|---|
| **Fix this issue** | Applies the finding's fix to the model **directly** — no Claude, no MCP link (see §4a). Shows what it will change, asks once, then runs it as a single transaction. |
| **Select all in model** | Selects every affected element of the finding in Revit, so Revit's own properties palette and filters work on them. |
| **Highlight in 3D section box** | Creates (or reuses) the 3D view **CC - DM Compliance 3D**, fits its section box around the affected elements with a 1.5 m margin, colours them red, selects them **and opens the view straight away** — the dashboard stays where it is. |
| **Highlight selected element** | The same, for just the elements picked in the element list. |
| **Copy prompt** | Copies the fix prompt for the selected finding — DM data and the runnable script included. |
| **Copy script only** | Copies just the C# that the prompt asks Claude to send with `send_code_to_revit`. |
| **Copy fix-all prompt** | Copies one prompt that walks Claude through every finding, worst first. |
| **Bind DM parameters** | Creates the DM shared parameters from the plugin's own data and binds them to the categories that need them — no DM file required (see §3). |
| **Export report** | Writes the HTML dashboard, the CSV and the prompt file (see §5) and opens the HTML. |
| **DM rule data** | Writes the DM data files to `Documents\CodeCompliance\DMKnowledgeBase` so they can be updated (see §6). |
| **Clear highlight** | Removes the red overrides from the compliance view. |
| **Start MCP server** | Switches the Revit MCP server on without leaving the dashboard, so a finding the tool does not fix on its own can be handed to Claude straight away. The button shows the port once it is running. |

---

## 3. The DM parameters — nothing to download or upload

The tool carries Dubai Municipality's data itself. It knows all 250 DM shared parameters
with their data types, so it **writes the Revit shared parameter file** rather than asking
for DM's file:

`Documents\CodeCompliance\DMKnowledgeBase\DM_SharedParameters.txt`

It is (re)written every time the audit runs. GUIDs are derived from the parameter name, so
the same parameter always gets the same GUID on every machine and every run. If your office
already works with DM's official shared parameter file, load that one first — existing
definitions with the same name are reused and only the missing ones are created from here.

**Bind DM parameters** in the dashboard creates every attribute the audit needs and binds
it, as an instance parameter, to exactly the categories that need it (walls, doors, rooms,
levels, project information …) in a single transaction. That clears the "attribute not
bound" findings without leaving Revit. The same operation is available as a script inside
the prompts, so Claude can do it over the MCP link instead.

The findings table has an **Auto-fix** column: *Yes* means **Fix this issue** can apply it.

---

## 4a. Fixing a finding without leaving Revit

The audit already knows which elements are wrong and what the right value is, so most findings
do not need Claude at all. **Fix this issue** makes the change itself, in native Revit API
calls — it works offline, needs no API key, and does not depend on the MCP command sets being
installed.

Three rules govern it:

- **Nothing is invented.** A DM "data sample" is an example, not this project's value. An
  attribute whose value cannot be read from the model is reported as skipped, never filled in.
  Only `IsExternal`, `LoadBearing`, `FireRating`, `IfcMaterial`, `Status` and
  `SpaceUsageDescription` are derivable, and each is derived exactly as the fix script does.
- **Nothing is deleted**, and the change runs as **one named transaction** — Ctrl+Z puts the
  model back.
- **Decisions stay yours.** Renaming, splitting columns, remodelling an object, resolving a
  clash with a link, deleting a redundant room and purging are refused with the reason; those
  findings carry their prompt instead.

What it applies:

| | Fix |
|---|---|
| Bind parameter | Creates the DM shared parameters of that finding and binds them to its categories |
| Set parameter | Fills the attribute on the flagged elements, deriving the value from the model |
| RMP-01 | Wall base offset down to SSL; wall top stopped under the slab above (thickness read from the floors of that level); unconnected heights constrained to the level above |
| RMP-03 | Room Bounding switched on (instance, or the family type where the flag lives there) |
| RMP-04 | *Place Rooms Automatically* on the storeys that carry uncovered area — only adds rooms |
| RMP-05 / RMP-06 | Re-hosts the elements onto the right level, compensating the offset so the geometry does not move |
| RMP-09 / RMP-13 | Writes `IfcExportAs` (and the predefined type) on the element **types** |
| RMP-10 | Sets Upper Limit and Limit Offset so the room reaches its ceiling |
| RMP-14 | Creates the dummy level (Building Story cleared) and moves the elevated elements onto it |

After a successful fix the audit re-runs by itself, so the finding disappears — or shows what
is left, with the skipped elements and the reason.

---

## 4. Fixing findings with Claude (Revit MCP)

Each finding's prompt is self-contained. It carries:

- the finding, the DM clause behind it and the affected element ids;
- **the DM data the fix needs** — the attribute's data type, property set and sample value,
  the IFC4 `PredefinedType` and `ObjectTypeOverride` enumerations for that element class,
  the Appendix C vocabularies (zones, unit usages, building occupancies), and the level,
  object and file naming tables. For space usage codes it carries a **proposed mapping for
  the room names actually found in this model**;
- **a ready-to-run C# script** for the revit-mcp tool `send_code_to_revit`, written for that
  host: C# 5, no transaction of its own (the host opens one), `document` in scope, and it
  returns a summary of what it changed.

For the **recommended modelling practices** the script is the model change itself, written
against the same host: re-constrain the walls to the slab above (the slab thickness is read
from the floors of that level), place the missing rooms with *Place Rooms Automatically*,
switch Room Bounding on, re-host elements onto the level their geometry sits on while
compensating the offset so nothing moves, raise the rooms to their ceiling, write
`IfcExportAs = IfcCovering` / `DontExport` on the element types, create the dummy level for a
split storey. Where the change needs a decision — splitting a column, remodelling a pergola,
moving geometry that clashes with a link, deleting a redundant room, purging — the prompt
carries DM's own Revit steps instead of a script and asks before anything is touched.

Where the value can be read from the model, the script derives it instead of asking:
`IsExternal` from the wall function (or the host wall for doors and windows), `FireRating`
from the element type, `LoadBearing` from the structural flag and category, `IfcMaterial`
from the material, `Status` from the phase, `SpaceUsageDescription` from the code on the
same room. Anything that cannot be derived is reported back as skipped, with the elements
listed, instead of being guessed.

Workflow:

1. Click **Start MCP server** in the dashboard footer — or **APG Revit Plugins ▸ Revit MCP ▸
   MCP Server** (see [docs/REVIT_MCP.md](REVIT_MCP.md) for the one-time setup).
2. In the dashboard, select a finding and click **Copy prompt**.
3. Paste it into Claude Desktop (or Claude Code). Claude sends the script to Revit and
   reports what it changed.
4. Click **Run audit** again to see the finding disappear.

Start with the binding findings (or press **Bind DM parameters**): once the attributes exist
on the categories, every value fix can run. Then the gate level, the geo-referencing and the
usage codes unblock most of what is left.

---

## 5. Report files

Written to `Documents\CodeCompliance`:

| File | Content |
|---|---|
| `<model>_DM_Compliance_<stamp>.html` | Dashboard for reading and sharing: tiles, the checks that ran, every finding with its element ids and its prompt |
| `<model>_DM_Compliance_<stamp>.csv` | One row per finding with the practice id and the full element id list — use it to track the fixes |
| `<model>_DM_Compliance_Prompts_<stamp>.txt` | The fix-all prompt plus one prompt per finding |

---

## 6. Keeping the rules current

Dubai Municipality revises the standard every one to three months (the shipped data is
version **1.4**, last DM change 2026-06-08). Click **DM rule data** in the dashboard: the
files are written to `Documents\CodeCompliance\DMKnowledgeBase`. Any file you replace there
wins over the copy embedded in the plugin, so a new DM revision only means swapping data
files — no new plugin build.

---

## 7. What the audit cannot see

- Compliance is finally assessed on the **exported IFC**, not on the Revit file. The audit
  anticipates the exporter, it does not replace a check of the exported file in a viewer.
- The IFC export setup itself is not readable through the Revit API, so the DM export
  settings stay a checklist item in phase 8.
- The modelling practices are checked on geometry the Revit API exposes cheaply: bounding
  boxes, constraints and parameters. The link clash check (RMP-11) therefore compares
  bounding-box volumes inside a time budget rather than running a full solid clash — it points
  at what to look at, it does not replace Navisworks.
- Duplicated elements **across** IFC files (the single-source rule) cannot be seen from a
  single Revit session.
- MEP element attributes are not checked: DM currently mandates only the architectural and
  structural models for submission.
- Project settings (units, coordinates, the IFC export setup) are changed in the Revit user
  interface, not by a script — those findings say so instead of shipping one.
