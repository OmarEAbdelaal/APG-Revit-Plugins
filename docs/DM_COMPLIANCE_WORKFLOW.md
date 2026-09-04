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

The audit runs in seven phases, mirroring DM's own offline self-assessment:

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
7. **Export readiness** — file naming convention and the ParcelId cross-check, imported CAD,
   CAD and Revit links, in-place families, the model warning count, and the IFC export setup
   checklist.

Findings are classified the way DM's own QA/QC checker reports them: **Critical** (blocks the
submission), **Error** (fix before submitting) and **Warning** (fix where feasible).

The audit is **read-only**. Nothing in the model changes until you decide to fix something.

---

## 2. The dashboard

Click **DM Compliance**. The audit runs immediately and the dashboard shows:

- **Five tiles** — critical, errors, warnings, how many elements need modification, and a
  submission-readiness percentage.
- **Permit stage** — Final permit (default) or Preliminary permit. DM requires different
  attributes at each stage, and the Appendix B "Required" column is read accordingly.
  *Include conditional attributes* adds the ones DM marks Conditional (unit data and similar).
- **A findings table** — severity, phase, scope, the issue, the **type of modification**
  (set parameter, load/bind parameter, rename, model change, project setup, review), the DM
  attribute involved and how many of the checked elements are affected. Filter by severity,
  by phase or by free text.
- **A detail panel** for the selected finding — what is wrong, the DM reference, what to
  change, example elements, and the ready-made **prompt for Claude**.

### Buttons

| Button | What it does |
|---|---|
| **Select in model** | Selects the affected elements in Revit, so Revit's own properties palette and filters work on them. |
| **Highlight in 3D section box** | Creates (or reuses) the 3D view **CC - DM Compliance 3D**, fits its section box around the affected elements with a 1.5 m margin, colours them red and selects them. The view opens when you close the dashboard. |
| **Copy prompt** | Copies the fix prompt for the selected finding — DM data and the runnable script included. |
| **Copy script only** | Copies just the C# that the prompt asks Claude to send with `send_code_to_revit`. |
| **Copy fix-all prompt** | Copies one prompt that walks Claude through every finding, worst first. |
| **Bind DM parameters** | Creates the DM shared parameters from the plugin's own data and binds them to the categories that need them — no DM file required (see §3). |
| **Export report** | Writes the HTML dashboard, the CSV and the prompt file (see §5) and opens the HTML. |
| **DM rule data** | Writes the DM data files to `Documents\CodeCompliance\DMKnowledgeBase` so they can be updated (see §6). |
| **Clear highlight** | Removes the red overrides from the compliance view. |

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

Where the value can be read from the model, the script derives it instead of asking:
`IsExternal` from the wall function (or the host wall for doors and windows), `FireRating`
from the element type, `LoadBearing` from the structural flag and category, `IfcMaterial`
from the material, `Status` from the phase, `SpaceUsageDescription` from the code on the
same room. Anything that cannot be derived is reported back as skipped, with the elements
listed, instead of being guessed.

Workflow:

1. **APG Revit Plugins ▸ Revit MCP ▸ MCP Server** — start the server (see
   [docs/REVIT_MCP.md](REVIT_MCP.md) for the one-time setup).
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
| `<model>_DM_Compliance_<stamp>.csv` | One row per finding with the full element id list — use it to track the fixes |
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
  settings stay a checklist item in phase 7.
- Duplicated elements **across** IFC files (the single-source rule) cannot be seen from a
  single Revit session.
- MEP element attributes are not checked: DM currently mandates only the architectural and
  structural models for submission.
- Project settings (units, coordinates, the IFC export setup) are changed in the Revit user
  interface, not by a script — those findings say so instead of shipping one.
