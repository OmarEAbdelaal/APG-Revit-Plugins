---
name: revit-plugin-dev
description: >
  Develop the APG Revit Plugins suite in this repo: project structure, adding
  commands and ribbon buttons, multi-version Revit API rules (2024-2027), and how to
  compile/verify changes. Use whenever writing or modifying C# code under
  src/CodeCompliance, adding features, or fixing build errors.
---

# Revit Plugin Development (APG Revit Plugins)

## What this project is

The APG Revit Plugins suite (author: Omar Elsayed): one ribbon tab "APG Revit Plugins"
with one panel per plugin. Current plugins: "Code Compliance – Fire Fighting" (egress
analysis: escape stairs → travel paths → fire-rating report) and "Ramp Creator"
(Dubai BC Annex B parking ramps). One C# codebase targets **Revit 2024, 2025, 2026
and 2027** via build configurations.
See `docs/EGRESS_WORKFLOW.md` / `docs/RAMP_WORKFLOW.md` for user workflows and
`docs/ARCHITECTURE.md` for design.

## Critical environment fact: compile via CI, not locally

The remote session container has **no .NET SDK** and Revit API packages are
Windows-only. The compile loop is:

1. Edit code.
2. Commit and push to the working branch (`claude/**` branches trigger CI).
3. The GitHub Actions workflow **Build installer** (`.github/workflows/build-installer.yml`)
   builds ALL FOUR Revit targets on a Windows runner (~2 min).
4. Check the run via the GitHub MCP tools (`actions_list` / `actions_get` /
   `get_job_logs`). Grep the saved log for `error |Build failed for|Included Revit targets`.
5. A successful run means the code compiles against all four real Revit APIs and
   uploads an `APG-Revit-Plugins-Setup` installer artifact.

Never claim code compiles until CI is green. If one target fails, `build-installer.ps1`
excludes it from the installer with a warning — always verify the log line
`Included Revit targets: Release R24, Release R25, Release R26, Release R27`.

## Project layout

```
src/CodeCompliance/
  App.cs                      IExternalApplication - ribbon UI ONLY, no model logic.
                              Tab "APG Revit Plugins", one panel per plugin; a new
                              plugin = a new panel here
  RibbonIcons.cs              Loads embedded APG icons (Resources/*.png)
  Resources/*.png             Brand assets - regenerate via tools/gen-icons.ps1
  Commands/*.cs               One IExternalCommand per ribbon button (thin entry points)
  Core/*.cs                   Model logic (stairs, egress, ramps) - no UI
  Reporting/*.cs              Schedules + HTML/CSV export
  UI/*.cs                     WPF windows - CODE-ONLY WPF, no XAML (see rules below);
                              style with UI/ApgTheme.cs (header band, buttons, palette)
install/CodeCompliance.addin  Manifest (do not change the AddInId GUID - it keeps
                              "Always Load" trust and installer identity)
installer/                    Inno Setup script + build script + apg.ico
tools/gen-icons.ps1           Regenerates all APG brand assets
```

## Multi-version rules (the part that breaks builds)

| Configuration | Revit | TargetFramework |
|---|---|---|
| `* R24` | 2024 | `net48` |
| `* R25` | 2025 | `net8.0-windows` |
| `* R26` | 2026 | `net8.0-windows` |
| `* R27` | 2027 | `net10.0-windows` (NOT net8 — learned the hard way, NU1202) |

API references come from `Nice3point.Revit.Api.RevitAPI(+UI)` NuGet packages with a
`$(RevitVersion).*` wildcard. Each build defines `REVIT2024`…`REVIT2027` constants for
`#if` guards when APIs diverge.

Write code that compiles on ALL four targets. Verified-safe API choices:

- `TransactionMode.Manual` only — `ReadOnly`/`Automatic` were removed from the API.
- `ElementId.Value` (long) and `new ElementId(long)` — fine on 2024+; never `IntegerValue`.
- `SpecTypeId.*` / `GroupTypeId.*` (ForgeTypeId) — never `ParameterType` /
  `BuiltInParameterGroup` enums (removed).
- `UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Meters)` for units
  (internal units are feet).
- Element lengths: sum `curve.Length` from `element.get_Geometry(new Options())` —
  robust across versions; avoid guessing built-in length parameters.
- Door fire rating: `BuiltInParameter.DOOR_FIRE_RATING` on the **type** (fall back to
  `LookupParameter("Fire Rating")` on instance then type).
- Path of Travel: `Autodesk.Revit.DB.Analysis.PathOfTravel.Create(view, start, end)` in a
  `ViewPlan` only; wrap in try/catch (throws on unroutable points);
  `RouteAnalysisSettings.GetRouteAnalysisSettings(doc)` to manage ignored categories.
- C# 12 features are fine on net48 (`LangVersion=latest`), but avoid APIs missing from
  .NET Framework's BCL.

## WPF rule

Windows are built **in C# code, no XAML** (see `UI/EscapeStairsWindow.cs`) so the same
file compiles for net48/net8/net10 without XAML-compilation differences. Keep it that way.
`UseWPF` is already enabled in the csproj. In UI files do not `using Autodesk.Revit.DB`
(Grid/Binding name clashes) — reference Revit types via full namespace if needed.

## Adding a new command (checklist)

1. Create `Commands/<Name>Command.cs`: `[Transaction(TransactionMode.Manual)]`,
   implement `IExternalCommand`. Null-check `ActiveUIDocument`. Wrap writes in
   `using var t = new Transaction(doc, "...")`.
2. Register a `PushButtonData` in `App.CreateRibbon` — unique button name
   `CodeCompliance_<Name>`, full class name string must match exactly.
3. Put reusable logic in `Core/`, not in the command.
4. Push → verify CI green → tell the user to close Revit and reinstall.

## Conventions

- Plugin-created Path of Travel elements carry Mark prefix `CC - ` (used to find/replace them).
- The escape-stair parameter is `CC_IsEscapeStair` (shared param, Yes/No, Stairs category,
  bound by `EscapeStairService.EnsureParameter` inside a transaction).
- Schedules created by the plugin are named `CC - ...`.
- Reports export to `Documents\CodeCompliance\`.
- User-facing dialogs: `TaskDialog`, always with a clear next step.
