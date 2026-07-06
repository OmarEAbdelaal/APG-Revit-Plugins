# Architecture

## Goal

One code base that builds and runs in **Revit 2024, 2025, 2026 and 2027**, structured so a
fire-fighting compliance rule engine can be plugged in without reworking the foundation.

## Multi-version strategy

Revit versions differ in two ways that matter to an add-in:

1. **Runtime**: Revit 2024 hosts .NET Framework 4.8; Revit 2025/2026 host .NET 8;
   Revit 2027 hosts .NET 10.
2. **API surface**: small breaking changes between yearly releases.

Both are handled in `src/CodeCompliance/CodeCompliance.csproj` through *build
configurations* rather than separate projects:

- Configurations `Debug|Release R24…R27` set `RevitVersion` (2024–2027) and the matching
  `TargetFramework` (`net48`, `net8.0-windows` or `net10.0-windows`).
- Revit API references come from the `Nice3point.Revit.Api.*` NuGet packages with a
  version wildcard (`$(RevitVersion).*`), so no local Revit installation or SDK download
  is needed to compile.
- Each build defines a `REVIT2024` / `REVIT2025` / … constant. Version-specific API
  differences are isolated with `#if` blocks, e.g.:

  ```csharp
  #if REVIT2024
      var id = element.Id.IntegerValue;     // ElementId.IntegerValue removed later
  #else
      var id = element.Id.Value;
  #endif
  ```

- Output goes to `bin\<Configuration>\` and, on Windows, a post-build target deploys
  straight into `%APPDATA%\Autodesk\Revit\Addins\<version>\` for instant testing.

## Runtime structure

```
Revit startup
  └─ reads install/CodeCompliance.addin            (manifest)
       └─ loads CodeCompliance.dll
            └─ App : IExternalApplication           (App.cs)
                 └─ creates ribbon tab "APG Revit Plugins"
                      ├─ panel "Code Compliance – Fire Fighting"
                      │    Escape Stairs / Travel Paths / Egress Report / Model Check
                      ├─ panel "Ramp Creator"  → Parking Ramp
                      └─ panel "APG"           → About APG
```

- `App` does **UI registration only** — no model logic lives there.
- Each ribbon button maps to an `IExternalCommand` in `CodeCompliance.Commands`.
- `FireFightingCheckCommand` currently only reads the model (counts elements, opens no
  transaction); it is the placeholder where the rule engine will be invoked.

## Planned structure for the rule engine (next phase)

When the detailed compliance logic is specified, the intended shape is:

```
src/CodeCompliance/
    Commands/                  thin entry points (unchanged)
    Core/
        Model/                 extraction of FF systems from the Revit model
                               (sprinkler networks, risers, pumps, zones, coverage)
        Rules/                 one class per code rule; each rule consumes the
                               extracted model and emits findings
        IRule.cs               Check(FireFightingModel) -> IEnumerable<Finding>
        Finding.cs             severity, message, code reference, element ids
    Reporting/
        ReviewCommentWriter    on-plan annotations / tagging of failing elements
        ReportExporter         exportable summary (e.g. PDF/CSV/HTML)
    UI/
        Settings window (code edition, units, project parameters)
```

Design principles for that phase:

- **Rules are data-driven and isolated** — adding a code clause means adding one rule
  class (or one rule definition), never touching extraction or reporting.
- **Extraction is separated from evaluation** so the same model snapshot can be checked
  against many rules cheaply, and rules can be unit-tested without Revit.
- **Findings reference ElementIds**, so the UI can zoom to / highlight offending elements
  and reporting can annotate plans.
