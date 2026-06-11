# Code Compliance – Fire Fighting (Revit Plugin)

A Revit add-in that reviews **fire-fighting designs** against applicable codes and produces
comprehensive review comments on plans.

Supports **Revit 2024, 2025, 2026 and 2027** from a single code base.

## What you get right now

After installing and starting Revit you will see a new ribbon tab:

```
Code Compliance
└── Fire Fighting (panel)
    ├── Escape Stairs  – detect all stairs, tick which are escape stairs
    │                    (saved to the CC_IsEscapeStair parameter)
    ├── Travel Paths   – create Path of Travel lines from the most remote point of
    │                    every room to the nearest escape stair, through doors
    ├── Egress Report  – measure paths, detect fire ratings of doors on the routes,
    │                    create schedules and export an HTML + CSV report
    ├── Model Check    – counts fire-protection elements (installation test)
    └── About          – add-in version and info
```

See [docs/EGRESS_WORKFLOW.md](docs/EGRESS_WORKFLOW.md) for the 3-step egress analysis guide.

## For end users: one-click installer

Download `CodeComplianceSetup-<version>.exe` (from the GitHub **Releases** page, or from the
**Actions → Build installer** workflow artifacts) and double-click it. No admin rights
needed — the installer detects which Revit versions (2024–2027) are on the machine and
deploys to those automatically. Uninstall from Windows **Settings → Apps** as usual.

To build the installer yourself (needs [Inno Setup 6](https://jrsoftware.org/isdl.php)):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

## Quick start (Windows, Visual Studio 2022 or newer)

1. Clone the repository and open `CodeCompliance.sln`.
2. In the configuration dropdown pick the configuration matching your Revit version,
   e.g. **`Debug R25`** for Revit 2025.
3. Build (`Ctrl+Shift+B`). The post-build step automatically copies the add-in into
   `%APPDATA%\Autodesk\Revit\Addins\<version>\`.
4. Start Revit. When asked about loading the new add-in, choose **Always Load**.
5. Open any model and click **Code Compliance → Run FF Check**.

Alternatively, install from the command line without opening Visual Studio:

```powershell
powershell -ExecutionPolicy Bypass -File install\install.ps1 -RevitVersions 2025
```

See [docs/INSTALLATION.md](docs/INSTALLATION.md) for details and troubleshooting.

## Repository layout

```
CodeCompliance.sln                  Visual Studio solution (8 configurations: Debug/Release × R24–R27)
src/CodeCompliance/
    CodeCompliance.csproj           Multi-version project (one codebase, 4 Revit targets)
    App.cs                          IExternalApplication – builds the ribbon UI
    Commands/
        EscapeStairsCommand.cs      Step 1: detect stairs + mark escape stairs
        TravelPathsCommand.cs       Step 2: create travel distance paths
        EgressReportCommand.cs      Step 3: schedules + HTML/CSV report
        FireFightingCheckCommand.cs Element count summary (installation test)
        AboutCommand.cs             About dialog
    Core/
        EscapeStairService.cs       Stair detection + CC_IsEscapeStair parameter
        EgressAnalysisService.cs    Path of Travel creation, lengths, doors on path
    Reporting/
        EgressReportWriter.cs       HTML + CSV export
        ScheduleBuilder.cs          Revit schedules
    UI/
        EscapeStairsWindow.cs       Stair selection dialog (WPF)
install/
    CodeCompliance.addin            Revit add-in manifest
    install.ps1                     Build + install for any/all Revit versions
    uninstall.ps1                   Remove the add-in
installer/
    CodeCompliance.iss              Inno Setup script for the end-user setup.exe
    build-installer.ps1             Builds all targets + compiles the installer
.github/workflows/
    build-installer.yml             CI: builds the setup.exe on every push / release tag
docs/
    INSTALLATION.md                 Install, test and troubleshooting guide
    ARCHITECTURE.md                 How multi-version targeting works + roadmap
```

## How multi-version support works

| Configuration | Revit version | Target framework |
|---------------|---------------|------------------|
| `* R24`       | 2024          | .NET Framework 4.8 |
| `* R25`       | 2025          | .NET 8 |
| `* R26`       | 2026          | .NET 8 |
| `* R27`       | 2027          | .NET 10 |

Each configuration compiles the same source against the matching Revit API NuGet packages
and defines a `REVIT20XX` constant for the rare places where the API differs between
versions. Details in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Roadmap

- [x] Multi-version project skeleton (2024–2027)
- [x] Ribbon UI + installable test command
- [x] End-user installer (.exe) + CI build
- [x] Escape stair detection & tagging (CC_IsEscapeStair parameter)
- [x] Travel distance paths (room → escape stair, through doors)
- [x] Door fire-rating detection on escape routes
- [x] Egress schedules + HTML/CSV report
- [ ] Pass/fail rules (max travel distance, required door ratings — values to be specified)
- [ ] Two-exit / alternative route checks
- [ ] Review-comment annotations on plans

## Author

Omar E. Abdelaal
