# Installation & Testing Guide

## Prerequisites

- Windows 10/11
- One or more of: Autodesk Revit 2024, 2025, 2026, 2027
- To build from source: **Visual Studio 2022 (17.8+)** or the .NET SDK
  (SDK 8 for Revit 2024–2026 targets, SDK 10 for the Revit 2027 target;
  no Revit SDK download needed — the Revit API references come from NuGet)

## Option A — Installer .exe (recommended for end users)

1. Get `APG-Revit-Plugins-Setup-<version>.exe`:
   - from the repository's **Releases** page (attached automatically when a `v*` tag is
     pushed), or
   - from **Actions → Build installer → latest run → Artifacts** (built on every push
     to the main branch), or
   - build it locally: install [Inno Setup 6](https://jrsoftware.org/isdl.php)
     (`winget install JRSoftware.InnoSetup`) and run
     `powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1`.
2. Double-click the .exe. No admin rights are required — it installs per-user.
3. The installer detects which Revit versions (2024–2027) exist on the machine and
   deploys the add-in only to those. If none are detected it installs for all versions,
   ready for when Revit is installed.
4. Uninstall later via Windows **Settings → Apps → APG Revit Plugins**.

## Option B — Build & install with Visual Studio (recommended for development)

1. Open `CodeCompliance.sln`.
2. In the toolbar configuration dropdown choose the configuration for your Revit version:

   | Your Revit | Choose |
   |------------|--------|
   | 2024 | `Debug R24` |
   | 2025 | `Debug R25` |
   | 2026 | `Debug R26` |
   | 2027 | `Debug R27` |

3. Build the solution. A post-build step copies everything into
   `%APPDATA%\Autodesk\Revit\Addins\<version>\` automatically — there is no separate
   install step during development.

## Option C — Install from the command line

From the repository root in a PowerShell window:

```powershell
# All Revit versions you have packages for:
powershell -ExecutionPolicy Bypass -File install\install.ps1

# A single version:
powershell -ExecutionPolicy Bypass -File install\install.ps1 -RevitVersions 2025

# Several versions, debug build:
powershell -ExecutionPolicy Bypass -File install\install.ps1 -RevitVersions 2024,2026 -Configuration Debug
```

The script builds the right configuration per version and copies the output to the
Revit Addins folder of the **current user** (no admin rights needed).

## What gets installed where

```
%APPDATA%\Autodesk\Revit\Addins\<version>\
├── CodeCompliance.addin            (manifest Revit reads at startup)
└── CodeCompliance\
    └── CodeCompliance.dll          (the add-in itself)
```

## Testing the installation

1. Start Revit. On first start a security dialog asks about the new add-in —
   choose **Always Load**.
2. An **APG Revit Plugins** tab should appear in the ribbon.
3. Open any project (a model with fire-protection content gives a more interesting
   result, but any model works).
4. Click **Model Check** on the *Code Compliance – Fire Fighting* panel. You should see
   a dialog titled *"Plugin is installed and working."* listing counts of sprinklers,
   pipes, fittings, accessories and mechanical equipment.
5. Click **About APG** to verify the version info.

If you see that dialog, the whole pipeline works (manifest → assembly load → ribbon →
command → Revit API access) and the project is ready for the real compliance logic.

## Uninstalling

```powershell
powershell -ExecutionPolicy Bypass -File install\uninstall.ps1            # all versions
powershell -ExecutionPolicy Bypass -File install\uninstall.ps1 -RevitVersions 2025
```

## Troubleshooting

**The APG Revit Plugins tab does not appear**
- Confirm both the `.addin` file and the `CodeCompliance` folder exist under
  `%APPDATA%\Autodesk\Revit\Addins\<version>\` (paste that into Explorer's address bar).
- Make sure the Revit version of the build matches the Revit you started
  (a `R25` build will not load in Revit 2024).
- Check Revit's journal file (`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <version>\Journals`)
  for add-in load errors.

**Revit says the add-in failed to load**
- For Revit 2024 the DLL must be built with a `R24` configuration (.NET Framework 4.8);
  2025–2027 builds use .NET 8 and will not load in 2024, and vice versa.
- If the DLL was downloaded (not built locally), Windows may block it:
  right-click the DLL → Properties → check **Unblock**.

**NuGet restore fails for a configuration**
- For a brand-new Revit release the API package may not be on NuGet yet. In that case
  edit `src/CodeCompliance/CodeCompliance.csproj` and replace the two `PackageReference`
  lines with direct references to `RevitAPI.dll` and `RevitAPIUI.dll` from
  `C:\Program Files\Autodesk\Revit <version>\` (set `Private`/Copy Local to false).
- A `NU1202` "not compatible" error means the target framework in the csproj doesn't
  match what that Revit version requires (2024: net48, 2025/2026: net8.0-windows,
  2027: net10.0-windows).
