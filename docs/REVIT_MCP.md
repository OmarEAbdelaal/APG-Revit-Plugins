# Revit MCP — connect Claude to Revit

The **Revit MCP** plugin of the APG Revit Plugins suite lets Claude (Claude Desktop or
Claude Code) read and drive an open Revit session through the
[Model Context Protocol](https://modelcontextprotocol.io). Three pieces work together:

```
Claude Desktop ──stdio──▶ MCP server (Node.js)  ──TCP 8080──▶ Revit
                          revit-mcp/build/index.js            APG Revit Plugins ▸ Revit MCP
                          exposes ~26 "tools" to Claude       JSON-RPC service + command sets
                                                               (RevitMCPCommandSet.dll, RevitMCPExtraCommands.dll)
```

| Piece | Where it lives | Who installs / updates it |
|---|---|---|
| Revit MCP plugin (ribbon panel, socket service, installer) | `CodeCompliance.dll` — part of the suite installer | APG-Revit-Plugins releases |
| MCP server (Node.js) | `%LOCALAPPDATA%\APGRevitPlugins\RevitMCP\server\` | **MCP Setup** button, from [OmarEAbdelaal/revit-mcp releases](https://github.com/OmarEAbdelaal/revit-mcp/releases) |
| Revit command sets (DLLs per Revit version) | `%LOCALAPPDATA%\APGRevitPlugins\RevitMCP\Commands\<Set>\<year>\` | same release, same button |

The server and the command sets are versioned and published together on GitHub. The plugin
checks that repository every time Revit starts and installs a newer release silently, so
new tools reach every user automatically (see [Updates](#updates)).

---

## 1. Requirements

- Windows 10/11, Revit 2024, 2025, 2026 or 2027.
- **Node.js 22.13 or newer** (LTS recommended) — <https://nodejs.org>. The MCP server is a
  Node program that Claude launches on demand; it has no native dependencies.
- **Claude Desktop** (<https://claude.ai/download>) or Claude Code.
- Internet access to `github.com` for the one-time install and for updates.

## 2. Install the suite

Run `APG-Revit-Plugins-Setup-<version>.exe` (Releases page of this repository). Start Revit
and choose **Always Load**. The **APG Revit Plugins** tab now has a **Revit MCP** panel with
two buttons: **MCP Server** and **MCP Setup**.

## 3. One-time setup (about two minutes)

1. Click **MCP Setup**.
2. Section 2 shows what is installed. Click **Install / Update from GitHub**. The plugin
   downloads the latest `revit-mcp-server-<v>.zip` and `revit-mcp-commands-<v>.zip`
   from the releases of `OmarEAbdelaal/revit-mcp` and unpacks them under
   `%LOCALAPPDATA%\APGRevitPlugins\RevitMCP\`.
3. Check the **Node.js** line. If it says *not found*, install Node.js LTS and reopen the window.
4. Click **Configure Claude Desktop**. This adds (or updates) a `revit-mcp` entry in
   `%APPDATA%\Claude\claude_desktop_config.json`, keeping every other MCP server you have.
   A `.bak` copy of the previous file is written next to it. The entry looks like:

   ```json
   {
     "mcpServers": {
       "revit-mcp": {
         "command": "C:\\Program Files\\nodejs\\node.exe",
         "args": ["C:\\Users\\<you>\\AppData\\Local\\APGRevitPlugins\\RevitMCP\\server\\build\\index.js"],
         "env": { "REVIT_MCP_PORT": "8080" }
       }
     }
   }
   ```

   For Claude Code or another MCP client, click **Copy config JSON** and paste it into that
   client's configuration (Claude Code: `claude mcp add-json revit-mcp '<the pasted object>'`).
5. **Restart Claude Desktop** (quit it from the tray icon, then start it again). The Revit
   tools appear under the tools icon of the chat box.

## 4. Daily use

1. Open your model in Revit.
2. Click **MCP Server** on the APG Revit Plugins tab. A dialog confirms *MCP server switched ON
   (port 8080)* and the number of commands loaded for your Revit version. Click the button
   again to switch it off. (Tick *Start automatically when Revit starts* in MCP Setup if you
   prefer it always on.)
3. In Claude, ask for something in the model, for example:
   - *"Say hello in Revit"* (connection test)
   - *"What view am I in, and how many walls are visible?"*
   - *"Tag all rooms in the current view"*
   - *"Color the walls by their Fire Rating parameter"*
   - *"Create levels at 0, 4 and 8 m"*

   Claude calls the MCP server, which connects to `localhost:8080` and runs the matching
   command inside Revit. Commands that change the model run in Revit transactions and can be
   undone with Ctrl+Z.

## 5. What Claude can do (tools)

| Tool (Claude) | Runs in Revit | What it does |
|---|---|---|
| `say_hello` | say_hello | Shows a dialog — connection test |
| `get_current_view_info` / `get_current_view_elements` | same | Active view properties / elements (filters by category, type, visibility) |
| `get_selected_elements`, `get_available_family_types` | same | Current selection, loaded family types |
| `ai_element_filter` | ai_element_filter | Query elements by category, level, parameters, bounding box |
| `create_point_based_element`, `create_line_based_element`, `create_surface_based_element` | same | Doors/windows/furniture, walls/beams/pipes, floors/ceilings/roofs |
| `create_grid`, `create_level`, `create_room`, `create_structural_framing_system` | same | Grids, levels, rooms, beam systems |
| `modify_element`, `operate_element`, `delete_element` | same | Change parameters; select/hide/isolate/color; delete |
| `color_elements`, `tag_all_walls`, `tag_all_rooms` | color_splash, tag_all_walls, tag_rooms | Colour by parameter value, tag walls/rooms in the view |
| `export_room_data`, `get_material_quantities`, `analyze_model_statistics` | same | Room schedule data, material take-off, model statistics |
| `edit_family` | edit_family | Open, inspect and edit a family, then reload it |
| `send_code_to_revit` | send_code_to_revit | Compile and run C# in the Revit context (advanced) |
| `search_modules`, `use_module` | built into the plugin | List the commands available in this Revit; run one by name |
| `store_project_data`, `store_room_data`, `query_stored_data` | — (server only) | Keep project/room snapshots in a local SQLite database |

The **MCP Setup** window lists every command the installed command sets provide, whether a
build exists for the running Revit version, and lets you switch individual commands off
(saved in `mcp-settings.json`; applied the next time the server starts).

## 6. Updates

- **Suite (the plugin itself)**: on startup the suite checks its own GitHub releases and
  shows a *Download update* notice, as before.
- **MCP server and command sets**: on startup (when *Update ... automatically* is ticked,
  the default) the plugin compares the installed version with the latest release of
  `OmarEAbdelaal/revit-mcp`. A newer release is downloaded and installed in the background;
  a dialog tells you when that happened. Restart Claude Desktop to load the new server; the
  new Revit commands are used the next time you switch the MCP server on.
- Manual: **MCP Setup ▸ Install / Update from GitHub** (stop the MCP server first — Revit
  locks loaded DLLs).

Publishing an update therefore only requires a new tag on the `revit-mcp` repository; see its
README for the release workflow.

## 7. Files and folders

```
%LOCALAPPDATA%\APGRevitPlugins\RevitMCP\
  server\                      MCP server (build\index.js, node_modules, package.json)
  Commands\
    RevitMCPCommandSet\        command.json + 2024\ 2025\ 2026\ 2027\ (DLLs)
    RevitMCPExtraCommands\     command.json + 2024\ 2025\ 2026\ 2027\ (DLLs)
  data\revit-data.db           SQLite data written by the server (store_* tools)
  Logs\mcp_yyyyMMdd.log        socket service log: every request and error
  installed.json               versions of the installed server / commands
  mcp-settings.json            port, auto-start, auto-update, disabled commands
%APPDATA%\Claude\claude_desktop_config.json     Claude Desktop MCP configuration
```

## 8. Troubleshooting

**Claude says the Revit tools are unavailable / no hammer icon**
- Claude Desktop must be fully restarted after *Configure Claude Desktop*.
- Check **MCP Setup ▸ Node.js**: Node 22.13+ must be installed. Claude runs the `command`
  from the config file, so the path to `node.exe` must exist.
- Claude Desktop ▸ Settings ▸ Developer shows the server log if the server fails to start.

**Tools exist but every call fails with "Could not connect to Revit on localhost:8080"**
- Switch **MCP Server** on in Revit (the dialog must say *switched ON*).
- Only one Revit session can own the port. Close the other session's server or change the
  port in MCP Setup (then *Configure Claude Desktop* again so the env variable follows).
- A firewall prompt may appear the first time; allow it for private networks (the service
  only listens on `localhost` unless *allowRemoteConnections* is set in `mcp-settings.json`).

**"Method xyz not found"**
- The command set for your Revit version is missing that command. Open **MCP Setup**: the
  *Revit 20xx* column says *no build* for commands without a DLL for this version. Install /
  Update to get the latest command sets.

**Install / Update fails with "the command DLLs are in use"**
- Revit locks command DLLs once loaded. Switch the MCP server off, or restart Revit and update
  before switching it on. Startup auto-update runs before any command is loaded.

**Where are the logs?**
- Revit side: `%LOCALAPPDATA%\APGRevitPlugins\RevitMCP\Logs\`.
- Server side: Claude Desktop ▸ Settings ▸ Developer ▸ Open logs folder (`mcp-server-revit-mcp.log`).

## 9. Developing new tools

A tool has two halves: a TypeScript tool in the MCP server (what Claude sees) and a Revit
command in a command set (what runs in Revit). Both live in the
[OmarEAbdelaal/revit-mcp](https://github.com/OmarEAbdelaal/revit-mcp) repository:

1. `src/tools/<name>.ts` — describe the tool with a zod schema and call
   `revitClient.sendCommand("<command_name>", params)`.
2. `revit-commands/RevitMCPExtraCommands/` — implement `<command_name>` as a
   `RevitMCPSDK.API.Base.ExternalEventCommandBase` subclass and list it in that set's
   `command.json`.
3. Push a tag `vX.Y.Z`. The repository's GitHub Actions workflow builds the server and the
   command sets for Revit 2024–2027 and attaches `revit-mcp-server-vX.Y.Z.zip` and
   `revit-mcp-commands-vX.Y.Z.zip` to the release. Every APG Revit Plugins user receives it
   automatically at the next Revit start.

The plugin loads commands by reflection (any public class with a `CommandName` property and
an `Execute(JObject, string)` method), so command sets built against any RevitMCPSDK version
work, and no SDK reference is needed in this repository.
