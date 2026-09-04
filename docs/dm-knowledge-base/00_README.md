---
title: Dubai Municipality BIM Compliance Knowledge Base
version: Aligned to Dubai BIM Standard v1.4 (last DM update 2026-06-08)
purpose: >
  Structured, machine-usable extraction of Dubai Municipality's BIM e-Submission
  requirements, for driving an automated Revit compliance-check tool via revit-mcp
  BEFORE exporting a model to IFC for DM permit submission.
compiled: 2026-09-04
source_folder: "G:\\My Drive\\03- Work\\BIM System\\DubaiBIMStandards"
---

# Dubai Municipality BIM Compliance Knowledge Base

This folder is the structured output of reviewing Dubai Municipality's official BIM
standard package (Dubai BIM Standard v1.4, its Technical Guides, the IDS ruleset, the
Appendices, and the Revit E-Submission templates). It is organized so that a Revit +
MCP compliance-check tool can consume it directly — as data, not prose — rather than
requiring an LLM or a human to re-read the PDFs each time.

## How this maps to a Revit/MCP compliance tool

The natural pipeline for a checker built on revit-mcp is:

1. **Project/Site/Building-level checks** — read `IfcProject`/`IfcSite`/`IfcBuilding`
   equivalents (Revit Project Information) against `05_ids_rules.json` filtered to
   those entities, plus `04_element_attribute_requirements/Project.csv`,
   `Topography (Site).csv`, `Building.csv`.
2. **Level (Storey) checks** — validate every level against the naming pattern in
   `02_naming_and_file_conventions.md` and the attributes in
   `04_element_attribute_requirements/Storey.csv`.
3. **Per-element checks** — for every modeled category (wall, door, slab, space,
   parking, etc.), map the Revit category to its required IFC class using
   `06_revit_specifics/revit_category_to_ifc.json`, then check required/conditional
   attributes from the matching CSV in `04_element_attribute_requirements/`.
4. **Space/Room usage checks** — validate `SpaceUsageCode`, `ZoneName`,
   `BuildingOccupancyUsageCode`, `UnitUsageCode`, etc. against the controlled
   vocabularies in `03_usage_codes/`.
5. **Machine rule pass** — run every applicable rule in `05_ids_rules.json` (127
   rules distilled from DM's own buildingSMART IDS file) — this is DM's own
   automated checker's rulebook, so passing it is the strongest pre-submission
   signal.
6. **Export/segregation/geolocation checks** — structural items that can't be
   checked from a single element's parameters: file naming, model segregation,
   georeferencing, IFC export settings. See `02_naming_and_file_conventions.md` and
   `07_export_and_georeferencing.md`.

## Folder contents

| File / folder | Content |
|---|---|
| `01_mandate_and_scope.md` | Who must submit BIM, when, and for which buildings (Circulars 9-1-2/2023 and 9-1-3/2026) |
| `02_naming_and_file_conventions.md` | File naming convention, level naming convention, file format/size rules |
| `03_usage_codes/` | Controlled vocabularies (CSV, from Appendix C): Building types/usages, Unit usages, Zone categories, Space usage codes, Unit Extra Info templates |
| `04_element_attribute_requirements/` | Per-IFC-class required/conditional attributes (CSV, from Appendix B) — one CSV per element type (Wall, Door, Room, Slab, Column, Parking, etc.) plus `Sheet2.csv` (Appendix A, Model Element Matrix — which elements are required per permit stage and their LOD) |
| `05_ids_rules.json` | All 127 machine-checkable rules from DM's official IDS file, parsed into plain JSON (entity, property set, property name, data type, cardinality, allowed values where enumerated) |
| `06_revit_specifics/` | `revit_category_to_ifc.json` (Revit category → IFC class export mapping), `revit_property_sets.json` (the "Building Permit" custom property set definitions), `revit_shared_parameters.json` (all 250 shared parameters DM provides, with data types) |
| `07_export_and_georeferencing.md` | Geo-referencing procedure, coordinate system, IFC export settings for Revit 2023/2024/2025, model segregation/splitting rules |
| `08_qaqc_self_assessment.md` | The self-assessment workflow DM expects before submission (QA/QC tool, Building Card generator, rule check), and what Critical/Error/Warning mean |
| `09_changelog_notes.md` | Standard version history — useful to detect when DM revises requirements and this KB needs a refresh |

## Key facts worth keeping in working memory

- **Current standard version: 1.4** (last changed 2026-06-08). Always check
  `BIMStandardVersion` attribute is set to the current value.
- IFC files must be **IFC4 schema, Reference View MVD**, units in **meters**, geo-referenced to **EPSG:3997 (WGS 84 / Dubai Local TM)**.
- Compliance is assessed on the **exported IFC**, not the native Revit file — so any Revit-side checker must simulate/anticipate what the IFC exporter will do (category mapping, property set mapping, PredefinedType/ObjectTypeOverride).
- The **IDS file is DM's actual automated-checker rulebook** — replicating those 127 rules inside Revit before export is the single highest-value check to build first.
