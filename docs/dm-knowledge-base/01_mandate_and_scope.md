---
title: BIM Mandate and Scope
sources: [DMBIMMandate_2023.pdf (Circular 9-1-2/2023), DMBIMMandate_2026.pdf (Circular 9-1-3/2026), DubaiBIMStandard.pdf]
---

# Who must submit BIM, and when

Dubai Municipality mandates **Open BIM submission** (IFC, per the Dubai BIM Standard)
alongside the building permit application. This is **in addition to**, not instead
of, the normal drawing/document submission — 2D drawings must be generated directly
from the BIM model.

## Circular 9-1-2 of 2023 (effective 1 Jan 2024) — initial scope

BIM submission mandatory for new building permits for:

1. Buildings/structures with height **> 20 storeys** (architectural design) or **> 40
   storeys** (structural design).
2. Buildings/structures/complexes with area **> 20,000 sqm** (architectural) or **>
   30,000 sqm** (structural).
3. Specialized buildings: hospitals, universities, and similar.
4. Government projects (excluding pure utility/service projects).

Voluntary submission for private/investment villas and other buildings outside scope
is explicitly encouraged (counts toward engineering-excellence ratings for the
consultancy).

## Circular 9-1-3 of 2026 (effective 1 April 2026) — expanded scope

Expands mandatory scope to:

**New buildings:**
- Buildings/structures/complexes with height **> Ground + 12 storeys**.
- Buildings/structures/complexes with built-up area **≥ 15,000 sqm**.

**Modifications to buildings previously licensed with BIM:**
- *Major modifications* (any of: adding/removing floors; adding ≥1,500 sqm; full
  design change; changing main building use; changing building location on plot) —
  BIM model required **with the modification permit application**.
- *Other modifications* — final BIM model required **before requesting the
  completion certificate**, and must reflect the as-built/approved work actually
  executed on site.

## Standing rules across both circulars

- BIM models must be submitted **simultaneously** with the other required licensing
  plans/drawings, and those drawings must be extracted directly from the BIM model
  to guarantee full consistency.
- Submission does not replace any other currently-required plan or document — it
  runs in parallel.
- Enquiries: `GeoDubai@dm.gov.ae`. Platform: `https://buildindubai.gov.ae/bim`.

## Practical implication for a compliance tool

Before running a full compliance check, the tool should first ask (or infer from
project data) whether the project actually falls under mandatory BIM scope by either
circular — this affects whether a failed check is a hard blocker or advisory. A
simple rule table:

| Trigger | Threshold | Mandatory since |
|---|---|---|
| Height (architectural) | > G+12 storeys | 2026-04-01 |
| Height (older threshold, still valid for pre-2026 projects) | > 20 storeys (arch) / > 40 storeys (struct) | 2024-01-01 |
| Built-up area | ≥ 15,000 sqm | 2026-04-01 |
| Built-up area (older threshold) | > 20,000 sqm (arch) / > 30,000 sqm (struct) | 2024-01-01 |
| Building type | Hospitals, universities, specialized buildings | 2024-01-01 |
| Ownership | Government projects (excl. utilities) | 2024-01-01 |
| Modification to BIM-licensed building | Major mod. (floors, +1,500sqm, full redesign, use change, relocation) | 2026-04-01 |
