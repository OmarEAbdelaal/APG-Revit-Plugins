# APG-Revit-Plugins — notes for Claude

The APG Revit Plugins suite (C#), author Omar Elsayed: one ribbon tab "APG Revit Plugins"
with one panel per plugin — "Code Compliance – Fire Fighting", "Ramp Creator",
"Magic Annotation" and "Revit MCP" — and one installer for the whole suite. One codebase, four targets:
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
