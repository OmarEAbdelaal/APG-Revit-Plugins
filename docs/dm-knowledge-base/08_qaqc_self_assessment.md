---
title: Self-Assessment / QA-QC Workflow (what DM's own tools check)
sources: [DubaiBIMTechnicalGuides.pdf Part 3]
---

# Why this matters for a Revit-side tool

DM expects modelers to run **offline self-assessment before online submission**.
A Revit-integrated compliance tool built on revit-mcp is essentially trying to
front-load this offline QA/QC step to the earliest possible point — ideally while
still modeling, rather than after IFC export. The categories below are exactly what
DM's own desktop QA/QC Checker and Building Card Generator report on, so replicating
them is the direct measure of "is this tool doing its job."

# DM's offline tools (context — a Revit tool should aim to make these unnecessary)

1. **QA/QC Checker** (desktop, folder-of-IFCs input) — checks completeness,
   correctness, validity of element information against Appendix B. Outputs a CSV
   with `EntityID` (or `-1` for type-level issues) classified as:
   - **Critical** — must be resolved before submission can be approved.
   - **Error** — non-critical but should be fixed.
   - **Warning** — low priority, fix where feasible.
2. **Building Card Generator** (desktop, folder-of-IFCs input) — extracts
   `IfcSpace` data to compute BUA/GFA/GA/NA, unit grouping, parking counts. Outputs
   `BuildingCardSummary.xlsx` (Project Info / Building Info / Unit Details / Parking
   Details tabs) plus debug CSVs (`Spaces.csv`, `Storeys.csv`) for root-causing
   discrepancies.
   - **Manual vs. automated area deviation tolerance: ±5%.** Anything beyond that
     needs justification or correction.
   - A `-1` automated area value in the summary means required `IfcSpace` data is
     missing — check the `Issues` column (AF) in `Spaces.csv`.
3. **Online platform (Build in Dubai)** — replicates the desktop QA/QC and Building
   Card results, plus:
   - Geolocation check (Maps tab → View Setback → verify footprint sits inside the
     parcel boundary).
   - **Rule Check (Compliance Check)** — automated Dubai Building Code compliance
     check. Can flag false positives; a flagged rule doesn't automatically block
     submission but must be reviewed. Rule-check accuracy is entirely dependent on
     upstream QA/QC and Building Card data being complete — garbage in, garbage out.

# Offline self-assessment checklist (what to replicate in Revit/MCP)

1. **Naming conventions**: every `IfcBuildingStorey` name matches the design levels
   in the 2D drawings, complies with the naming convention, has no dummy/reference
   levels leaking through, and is identical across AR/ST models.
2. **Geolocation & Gate Level**: `IfcSite` coordinates match Revit's Internal Origin;
   Gate/Ground level elevation matches the official DMD elevation.
3. **Geometry/visualization**: correct IFC class per element, nothing floating or
   mis-hosted to the wrong storey, no missing/duplicated/incomplete geometry, all
   internal spaces and accessible roofs/terraces have `IfcSpace`, no element spans
   multiple levels where it shouldn't, materials reasonably reflect design intent.
4. **General information (QA/QC)**: every mandatory attribute in Appendix B is
   populated for its element type — this is the bulk of what `05_ids_rules.json`
   encodes machine-checkably.
5. **Spaces/Units usage (Building Card)**: area calculations reconcile with manually
   submitted calculations within ±5%; units/amenities are grouped correctly; parking
   counts per level are accurate.

# Practical build order suggestion

Given this checklist, the highest-leverage first build for a revit-mcp tool is:
1. Level naming + Gate Level check (cheap, catches a very common rejection reason).
2. Run all 127 `05_ids_rules.json` rules against the model's parameters (before
   export) — this is DM's own machine rule set.
3. Space/room completeness + usage code validation against `03_usage_codes/`.
4. A local BUA/GFA/NA calculator from `IfcSpace`-equivalent Revit rooms, to
   pre-flag the ±5% deviation check before DM's platform does.
5. Only then, geometry-level checks (floating elements, level-hosting, clashes) —
   these need more Revit API depth (element bounding box vs. level elevation, clash
   detection) and are best done via `analyze_model_statistics` / `get_current_view_elements` / `color_elements` in revit-mcp to visually flag offenders.
