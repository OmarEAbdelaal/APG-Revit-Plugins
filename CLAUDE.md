# CodeCompliance_R_Plugin — notes for Claude

Revit add-in (C#) checking fire-fighting code compliance. One codebase, four targets:
Revit 2024 (net48), 2025/2026 (net8.0-windows), 2027 (net10.0-windows).

## Non-negotiables

- **No local .NET SDK in this environment.** Compile by pushing to the working branch:
  the "Build installer" GitHub Actions workflow builds all four Revit targets in ~2 min.
  Verify via GitHub MCP tools; code is not "done" until CI is green for all four targets.
- Use the GitHub MCP tools for everything GitHub (no `gh` CLI; public REST API is
  rate-limited from here).
- Never change `AddInId` in `install/CodeCompliance.addin` or `AppId` in
  `installer/CodeCompliance.iss` (breaks Revit trust + in-place updates).
- Version lives in two places: `CodeCompliance.csproj` `<Version>` and
  `CodeCompliance.iss` `MyAppVersion`. Bump both together.

## Skills

- `revit-plugin-dev` — project structure, adding commands, Revit API version rules.
- `revit-plugin-release` — installer, CI artifacts, version bumps, GitHub releases,
  user update instructions.

## Docs

`docs/ARCHITECTURE.md` (design), `docs/EGRESS_WORKFLOW.md` (user workflow),
`docs/INSTALLATION.md` (install/troubleshooting).
