---
title: Dubai BIM Standard — Version History Notes
source: Misc/Changelog.xlsx
---

# Currency check

Latest recorded change: **2026-06-08 — version updated from 1.3 to 1.4**, plus
document restructuring for navigation and consolidation of prior technical PDFs into
the single Technical Guides document. This confirms the documents extracted into
this knowledge base (all dated at or after this update) are the current version as
of this compilation (2026-09-04).

# Notable historical changes worth knowing (so a tool doesn't assume old rules)

- Max ZIP file size raised to **150 MB**.
- IDS v1 files added 2024-09-30 (`DMBIM_IFC4_V1.ids`, now superseded by the
  `DMBIM_IDS_V1.ids` used in this KB).
- Typo/enum fixes accumulated over time in Appendix B (e.g. `DOUNLELOADEDCORRIDOR` →
  `DOUBLELOADEDCORRIDOR`, `HandicapAccessible` typo on doors, `PubliclyAccessible`
  typo on spaces) — a reminder that **enum values should always be pulled live from
  the current Appendix B/C files**, never hardcoded from memory of an older version.
- `BuildingNum` / `BuildingPartNum` attributes were added later (2024-07-04) — any
  reference sample model older than that date will be missing them.
- `BuildingOccupancyUsageCode` / `BuildingOccupancyUsageDescription` were added to
  the Space entity on 2024-01-30.
- Unit area attributes and 8-digit Parcel IDs were later additions too.

# Maintenance recommendation

Because DM revises this standard periodically (roughly every 1-3 months based on
this history), the compliance tool's rule set should **not be hardcoded permanently
into the tool's source code** — it should load `05_ids_rules.json` and the
`03_usage_codes/` / `04_element_attribute_requirements/` CSVs as external data
files, so a future standard update only requires re-running this extraction and
swapping the data files, not rewriting the tool.
