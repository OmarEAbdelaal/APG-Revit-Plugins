# APG-Revit-Plugins — notes for Claude

The APG Revit Plugins suite (C#), author Omar Elsayed: one ribbon tab "APG Revit Plugins"
with one panel per plugin — "Code Compliance – Fire Fighting", "DM BIM Compliance",
"Ramp Creator", "Magic Annotation" and "Revit MCP" — and one installer for the whole suite. One codebase, four targets:
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

## Revit MCP module (src/CodeCompliance/Core/Mcp, docs/REVIT_MCP.md)

- The plugin hosts the Revit side of the Claude connector: `McpSocketService` (TCP JSON-RPC
  on port 8080) + `McpCommandHost` (loads command-set DLLs by reflection: any class with a
  `CommandName` property and `Execute(JObject, string)`; no RevitMCPSDK reference here).
- The MCP server (Node.js) and the command sets are NOT in this repo. They live in
  `OmarEAbdelaal/revit-mcp` and are downloaded from that repo's GitHub releases by
  `McpInstaller` (MCP Setup button + silent auto-update at Revit startup). Release asset
  names are a contract: `revit-mcp-server-v*.zip` and `revit-mcp-commands-v*.zip`.
- Newtonsoft.Json is compile-time only (`ExcludeAssets="runtime"`): Revit ships it.
- Local builds work when a .NET SDK is present (`dotnet build -c "Release R24"` etc.);
  otherwise rely on CI as before.

## DM BIM Compliance module (src/CodeCompliance/Core/Dm, docs/DM_COMPLIANCE_WORKFLOW.md)

- Audits an open model against the Dubai Municipality BIM e-submission requirements and is
  strictly read-only; `DmHighlightService` is the only part that writes (it creates the
  "CC - DM Compliance 3D" view and its section box).
- **The rules are data, not code**: `Resources/DmKnowledgeBase/*` holds DM's IDS rule set,
  the Appendix B attribute matrices and the Appendix C usage codes, embedded in the DLL and
  overridable from `Documents\CodeCompliance\DMKnowledgeBase`. DM revises the standard every
  1-3 months — update the data files, do not hardcode rules in C#.
- `DmAuditService` (+ `.Elements.cs`, `.Modelling.cs`) produces `DmFinding`s that carry element
  ids, the type of modification and a ready-made Revit MCP prompt built by `DmPromptBuilder`,
  plus a runnable fix script from `DmScriptBuilder` (+ `.Modelling.cs`).
- Category ↔ IFC entity ↔ Appendix B table mapping lives in `DmRuleCatalog`; add a category
  there rather than in the audit code.
- Phase 7 is DM's *Recommended Modelling Practices*: `modelling_practices.json` carries the 15
  practices (wording, severity, fix kind, thresholds); `DmAuditService.Modelling.cs` only
  implements the detection, keyed on the practice id (`RMP-01` … `RMP-15`), and
  `DmScriptBuilder.Modelling.cs` the fix script keyed on `DmFinding.FixData["target"]`.
- `DmFixService` (+ `.Modelling.cs`) applies a finding **directly** in native Revit API calls
  (the "Fix this issue" button): one named transaction, nothing deleted, and no value invented
  — an attribute that cannot be derived from the model is skipped, never filled with the DM
  sample. Renames, reviews, project settings, purging and room deletion are refused on purpose.
- The dashboard is **modeless**: every Revit call goes through `DmRevitTask` (an
  `ExternalEvent`), never straight from the WPF thread. `DmUiSettings` persists the options,
  the filters and the window geometry.
