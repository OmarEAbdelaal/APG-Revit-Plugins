---
title: Georeferencing, Coordination, and IFC Export Settings
sources: [DubaiBIMStandard.pdf Sec 4.1.2/7.7, DubaiBIMTechnicalGuides.pdf Part 2.6-2.7 and Part 5.1.4-5.1.6]
---

# Georeferencing

- Coordinate reference system: **EPSG:3997 — WGS 84 / Dubai Local Transverse
  Mercator (TM)**.
- All IFC files must carry easting, northing, and elevation (DMD-referenced) on
  `IfcSite`.
- Procedure (Revit): Build in Dubai portal → "My BIM Sessions" → enter Parcel ID →
  download parcel GIS package → extract `PARCELS.dxf` → set units to meters, save as
  DWG → Link CAD into Revit → Manage → Coordinates → Acquire Coordinates (from the
  linked DWG) → verify Project Base Point / Internal Origin alignment to
  `GA_GATE LEVEL` via a section view → Manage → Coordinates → "Specify Coordinates
  at Point" at the gate level, entering the project's DMD elevation in meters.
- Result: the exported `IfcSite` Z value must equal the Gate Level / DMD elevation.

**Validation rules for a tool**:
- `IfcSite` X/Y/Z (or Revit Internal Origin equivalent) is populated and non-zero
  (a common failure is an unmoved default origin).
- `GateLevel` project attribute is populated and matches the elevation assigned to
  the `GA_GATE LEVEL` level.
- Model footprint, when checked in the BID viewer, falls inside the parcel boundary
  — this is a platform-side check, but a local pre-check against the downloaded
  parcel geometry (DXF) is possible if that data is available to the tool.

# Coordination / clash detection

Full multidisciplinary coordination (Architecture, Structure, MEP, Façade, Interior,
Landscape) is expected even though only AR and ST are currently mandated for
submission — DM's stated intent is to catch design conflicts before they reach
construction. A tool can run this as a standard Revit clash/interference check
across linked models prior to IFC export.

# IFC export settings — Autodesk Revit

**Revit 2023–2024:**
1. File → Export → Options → IFC Options → Load `Dubai BIM E-Submission_Parameter
   Mapping` (category mapping file), preferably from a local drive.
2. File → Export → IFC → choose filename per naming convention → Modify Setup.
3. Import Setup → load the provided `.json` export configuration
   (`Dubai BIM E-Submission_IFC4_ReferenceView.json`) — already embedded if using the
   DM Revit template.
4. IFC version: always **IFC4 Reference View**.
5. Additional Content tab: ensure linked Revit models are **not** exported (each
   discipline/building model exports itself only).
6. Property Sets tab: load `Dubai BIM E-Submission_Property Sets.txt`.
7. Geographic Reference tab: coordinate base = **Shared Coordinates**, no overrides
   on Projected CRS.

**Revit 2025:** same target settings, different UI path — category mapping is
imported from the "General" tab's Category Mapping control (Import Template) instead
of via IFC Options; the rest (Property Sets, Geographic Reference = Shared
Coordinates, no Revit-link export) is identical.

**Key export JSON settings** (from `Dubai BIM E-Submission_IFC4_ReferenceView.json`,
also stored at `06_revit_specifics/ifc_export_config.json` in this KB):
- `ExportInternalRevitPropertySets: false` — don't leak internal Revit psets.
- `ExportIFCCommonPropertySets: true`, `ExportBaseQuantities: true`.
- `ExportUserDefinedPsets: true` (points at the Property Sets file).
- `SpaceBoundaries: 1`, `ExportRoomsInView: true`.
- `SplitWallsAndColumns: false`.
- `Export2DElements: false`, `ExportLinkedFiles: false`.

**Validation rules for a tool (pre-export, inside Revit via revit-mcp)**:
- Model units currently set to meters at time of export (or export routine
  temporarily converts).
- No Revit-linked models will be exported (Additional Content equivalent — check
  link settings).
- The IFC export setup selected matches the DM-provided one (by comparing key
  settings against `ifc_export_config.json` rather than trusting the setup name,
  since names can be typo'd or duplicated).
- Confirm `Export to IFC As` category/PredefinedType is deliberately set (not left
  default) for at least the elements flagged in the Appendix A element matrix as
  required, and that any element intentionally excluded has been explicitly set to
  `Export to IFC = No` rather than merely left unclassified.

# Model splitting mechanics (Revit)

- Splitting for file-size reasons uses **section boxes** to divide a single large
  model into parts. Elements must never be duplicated across parts.
- Where the AR model is split, the ST model does not need to be split too (each
  discipline's segregation is decided independently).
- All split parts share the same `BuildingNum`; `BuildingPartNum` stays empty for
  size-based splits (it's reserved for genuinely distinct building parts, e.g.
  podium vs. tower — see `02_naming_and_file_conventions.md`).
