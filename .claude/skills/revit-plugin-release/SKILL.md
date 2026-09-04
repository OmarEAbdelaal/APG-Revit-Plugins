---
name: revit-plugin-release
description: >
  Build, version and ship the APG Revit Plugins suite installer: CI artifacts,
  version bumps, GitHub releases, and user update instructions. Use when the user asks
  for an installer/.exe, a new version, a release, or how to install/update the plugin.
---

# Revit Plugin Release & Installer

## How the installer is produced

Every push to `main`/`master`/`claude/**` runs `.github/workflows/build-installer.yml`
on `windows-latest`:

1. `installer/build-installer.ps1` builds `Release R24/R25/R26/R27`
   (a failing target is skipped with a warning, not a hard failure — check the log).
2. Inno Setup 6 (preinstalled on the runner) compiles `installer/CodeCompliance.iss`
   → `APG-Revit-Plugins-Setup-<version>.exe` (one installer for the whole suite).
3. The exe is uploaded as artifact **APG-Revit-Plugins-Setup** (GitHub zips artifacts).

Get the download link for the user:
- Find the run: GitHub MCP `actions_list` (method `list_workflow_runs`,
  `resource_id: build-installer.yml`, filter by branch).
- Artifact id: `actions_list` (method `list_workflow_run_artifacts`, resource_id = run id).
- User-facing URL: `https://github.com/OmarEAbdelaal/APG-Revit-Plugins/actions/runs/<RUN_ID>/artifacts/<ARTIFACT_ID>`
  (requires GitHub login; downloads a zip containing the .exe).

Always verify in the job log: `Included Revit targets: Release R24, Release R25, Release R26, Release R27`
before sharing a link — a partially-built installer silently misses Revit versions.

## Versioning (two places, keep in sync)

- `src/CodeCompliance/CodeCompliance.csproj` → `<Version>x.y.z</Version>`
- `installer/CodeCompliance.iss` → `#define MyAppVersion "x.y.z"`

Bump both on every feature release. The About command shows the assembly version so
users can verify which build they run.

## Publishing a public release

Two ways, both ending in a GitHub Release with the exe attached
(`softprops/action-gh-release`, needs the existing `contents: write` permission):

- **From a workstation**: push a tag matching `v*` (e.g. `v1.5.0`).
- **From this session**: tag pushes are refused by the egress policy (HTTP 403 on
  `refs/tags/**` — branch pushes are fine), so dispatch the workflow instead and let it
  create the tag: GitHub MCP `actions_run_trigger` with method `run_workflow`,
  `workflow_id: build-installer.yml`, `ref: main`, `inputs: { release_tag: "v1.5.0" }`.
  Verify afterwards with `get_release_by_tag`.

Use either when the user wants a permanent public download link instead of an Actions
artifact.

## Installer behaviour (Inno Setup, `installer/CodeCompliance.iss`)

- Per-user install (`PrivilegesRequired=lowest`) — no admin, no UAC.
- Detects installed Revit versions (Program Files dir or existing Addins folder) and
  deploys only to those; installs all four when none detected.
- Files go to `%APPDATA%\Autodesk\Revit\Addins\<ver>\` (manifest) and
  `...\<ver>\CodeCompliance\` (DLL). Uninstaller registered in Settings → Apps.
- **Never change** `AppId` in the .iss or `AddInId` GUID in `install/CodeCompliance.addin`:
  same AppId = in-place updates; same AddInId = Revit keeps the "Always Load" trust.

## User update instructions (standard reply)

1. Close Revit (DLL is locked while running).
2. Download the new `APG-Revit-Plugins-Setup-<ver>.exe`, run it — it overwrites in
   place, no uninstall needed.
3. Start Revit, click **About APG** on the APG Revit Plugins tab to confirm the version.

Escape-stair choices and schedules live in the Revit model, so updates never lose user data.

## Timing expectations

A CI run takes ~2 minutes. When waiting, use a Monitor timer (~4-5 min) then poll the
run via MCP; the public GitHub REST API is rate-limited from the session container, and
`gh` CLI is unavailable — use the GitHub MCP tools.
