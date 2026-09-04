---
title: File Naming, Level Naming, and File Format Rules
sources: [DubaiBIMStandard.pdf Part 2 Sec 6-7, DubaiBIMTechnicalGuides.pdf Part 4]
---

# File format

- IFC files: **IFC4 schema**, `.ifc` extension, **IFC4 Reference View** MVD.
- Model units: **meters** (mandatory at export; convert back after export if the
  project works in other units for documentation).
- Native authoring files (RVT etc.) may be requested as supplementary info but are
  never the authoritative deliverable — the IFC is authoritative.
- ZIP package (containing all IFC files for one submission) must not exceed the
  platform limit — currently **150 MB**. Use OS "Send to → Compressed folder", not
  WinRAR/7zip (documented cause of upload errors).

# File naming convention (mandatory, all submissions)

Five fields joined by underscore:

```
PN{6-digit ProjectNumber}_BI{6-digit BuildingID}_PA{7-or-8-digit ParcelID}_{2-char Discipline}_{3-digit ModelNumber}
```

Example: `PN123456_BI123456_PA1234567_AR_001`

| Field | Format | Notes |
|---|---|---|
| Project Number | `PN` + 6 digits | Issued by the permitting authority |
| Building ID | `BI` + 6 digits | Sequential per project, starting at 000001 |
| Parcel ID | `PA` + 7 or 8 digits | From the Affection Plan; **must exactly match the `ParcelId` attribute inside the IFC file** — this is a cross-check point |
| Model Discipline | 2-char code | `AR` = Architecture, `ST` = Structural (only two disciplines currently mandated) |
| Model Number | 3 digits, starts at 001 | Used only when a single discipline model is split into multiple IFC files due to size/complexity |

**Validation rule for a tool**: parse the ParcelId out of the filename and diff it
against the `ParcelId` IfcProject/Building Permit property — any mismatch is a
guaranteed rejection per the standard.

# Level (Storey) naming convention

Two fields joined by underscore: `{LevelAbbreviation}_{LevelIdentification}`

- **Level Abbreviation**: UPPERCASE only. Standard set of abbreviations used in
  practice: `B1`, `B2`… (basement), `GR` or `GA_GATE LEVEL` (ground/reference —
  see below), `P1`… (podium/parking), `M1`… (mezzanine), `F1`, `F2`… (floor), `RF`
  (roof).
- **Level Identification**: free text describing the level, consistent project-wide
  (e.g. `FLOOR1`, `BASEMENT1`, `GROUND FLOOR`, `ROOF`).
- Example: `F1_FLOOR1`.

**Mandatory special level**: every model must include a level named with the
`GA_GATE LEVEL` designation (or equivalent used consistently) — this is the
project's primary vertical reference and must align with the Revit "Internal
Origin" and the official DMD elevation. Its `IfcSite` Z-value in the exported IFC
must equal the Gate Level value.

**Dummy/reference levels**: allowed in the native Revit model for modelling
convenience, but must be **excluded from IFC export** — in Revit, disable the
"Building Story" parameter for these levels so they don't export as
`IfcBuildingStorey`.

**Validation rules for a tool**:
- Every `Level` in the model with "Building Story" = Yes must have a name matching
  `^[A-Z0-9]+_[A-Za-z0-9 ]+$` (uppercase abbreviation, underscore, identification).
- Exactly one Gate Level should exist and its elevation should match the project's
  recorded DMD/GateLevel value.
- No two levels should have a name collision, and Architectural/Structural models of
  the same building must use **identical level names** (a common DM rejection
  reason).
- Reference/dummy levels must have "Building Story" disabled before export.

# Model segregation (how a project is split into IFC files)

- **Building-based segregation**: one IFC file = one building (one `IfcBuilding`).
  Never combine buildings in one file.
- **Discipline-based segregation**: Architectural and Structural must be **separate**
  IFC files (only these two disciplines currently mandated for submission, though
  full multidisciplinary coordination is still expected internally).
- **Single source of elements**: no element may exist in more than one IFC file
  (no duplication across discipline/part files).
- Where a building is split into multiple IFC parts (for size), each part gets its
  own file, numbered via the Model Number field, and `BuildingPartNum` is assigned
  incrementally per Building Storey.
- Segregation strategy by configuration:
  - **Single standalone building**: 2 IFC files (AR + ST). Large models may split
    the AR model further into multiple parts.
  - **Multiple towers sharing a podium**: podium/basement = separate model; each
    tower = separate model. `BuildingNum` constant across all; `BuildingPartNum`
    incremented per part.
  - **Multiple buildings in one parcel**: each building = separate Revit model +
    separate IFC file. `BuildingNum` incremented per building; `BuildingPartNum`
    left empty (this is a different attribute from file-splitting-by-size).

**Validation rules for a tool**: confirm the model's `BuildingNum`/`BuildingPartNum`
scheme matches one of the three configurations above before export; flag any
element that appears hosted to a level/building outside its expected file scope
(cross-file duplication is invisible from a single Revit session, so this really
needs to be checked at the IFC level with an external diff, or via `store_project_data`/`query_stored_data` in revit-mcp to record per-file element GUIDs across sessions).
