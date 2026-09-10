# Revit MCP — connect Claude and ChatGPT to Revit

The **Revit MCP** plugin of the APG Revit Plugins suite lets an AI assistant — Claude
(Claude Desktop or Claude Code), ChatGPT, or any other MCP client — read and drive an open
Revit session through the [Model Context Protocol](https://modelcontextprotocol.io).
Three pieces work together:

```
Claude / ChatGPT ─stdio─▶ MCP server (Node.js)  ──TCP 8080──▶ Revit
                          revit-mcp/build/index.js            APG Revit Plugins ▸ Revit MCP
                          exposes ~26 "tools" to the AI       JSON-RPC service + command sets
                                                               (RevitMCPCommandSet.dll, RevitMCPExtraCommands.dll)
```

The plugin writes the configuration of **Claude Desktop** and **Claude Code** itself. Every
other MCP client — ChatGPT included — takes exactly the same entry: **MCP Setup ▸ Copy config
JSON** puts it on the clipboard.

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
- **Claude Desktop** (<https://claude.ai/download>), Claude Code, ChatGPT, or any other
  MCP client.
- Internet access to `github.com` for the one-time install and for updates.

**Node.js is not a prerequisite.** The MCP server is a Node program, but the download from
GitHub carries its own Node.js runtime (`server\runtime\node.exe`, the current LTS), and the
plugin points Claude at that executable. If a machine ever ends up without it, the plugin
downloads Node.js from nodejs.org by itself and verifies the checksum. An existing Node.js
installation is used only as a last resort.

## 2. Install the suite

Run `APG-Revit-Plugins-Setup-<version>.exe` (Releases page of this repository). Start Revit
and choose **Always Load**. The **APG Revit Plugins** tab now has a **Revit MCP** panel with
two buttons: **MCP Server** and **MCP Setup**.

## 3. One-time setup (about two minutes)

1. Click **MCP Setup**.
2. Section 2 shows what is installed. Click **Install / Update from GitHub**. The plugin
   downloads the latest `revit-mcp-server-<v>.zip` and `revit-mcp-commands-<v>.zip`
   from the releases of `OmarEAbdelaal/revit-mcp`, unpacks them under
   `%LOCALAPPDATA%\APGRevitPlugins\RevitMCP\`, and **writes the Claude configuration for you**
   as the last step. The **Node.js** line then shows the runtime that came with the download.
3. If you want to write the configuration again by hand (after changing the port, for example),
   click **Configure Claude**. If Claude Desktop is open it offers to close it, write, and start
   it again — see [Why the entry must be written while Claude is closed](#why-the-entry-must-be-written-while-claude-is-closed).
   It adds or updates the `revit-mcp` entry in
   `%APPDATA%\Claude\claude_desktop_config.json` and, when Claude Code is installed, in
   `%USERPROFILE%\.claude.json` — keeping every other MCP server and every other setting in
   those files. A `.bak` copy is written first, and the result is read back and verified.
   The entry looks like:

   ```json
   {
     "mcpServers": {
       "revit-mcp": {
         "command": "C:\\Users\\<you>\\AppData\\Local\\APGRevitPlugins\\RevitMCP\\server\\runtime\\node.exe",
         "args": ["C:\\Users\\<you>\\AppData\\Local\\APGRevitPlugins\\RevitMCP\\server\\build\\index.js"],
         "env": { "REVIT_MCP_PORT": "8080" }
       }
     }
   }
   ```

   For ChatGPT or another MCP client, click **Copy config JSON** and paste the entry into that
   client's MCP configuration (Claude Code: `claude mcp add-json revit-mcp '<the pasted object>'`).
   The entry is the same everywhere — it is the command line that starts the local MCP server.
4. **Restart Claude Desktop** so it reads the configuration: the **Restart Claude Desktop**
   button in MCP Setup does it for you, or quit Claude from the tray icon and start it again.
   The Revit tools then appear under the tools icon of the chat box.

### Why the entry must be written while Claude is closed

Claude Desktop reads `claude_desktop_config.json` when it starts and keeps it in memory. Every
time one of its own settings changes it writes that copy back over the file — which silently
undoes an entry added from outside meanwhile. The change looks applied (the file really does
contain it) and then reappears without `revit-mcp` minutes later.

The plugin therefore offers to close Claude Desktop, write the configuration and start it again.
That is the only order that survives. If you choose to write anyway, MCP Setup says so plainly
and you can close Claude and press **Configure Claude** once more.

### Several Windows accounts on one computer

Everything the connector needs lives under the profile of the account running Revit — the
server and command sets in `%LOCALAPPDATA%`, the settings, and the Claude configuration in
that account's `%APPDATA%\Claude`. Nothing is shared between accounts and nothing is baked in
at build time: **every path written into the configuration is resolved from the account running
Revit at that moment**, so one user's folder can never end up in another user's file.

The practical consequence is that each account provisions itself. The first time Revit starts
under an account that has never used Revit MCP, the plugin downloads the server, the command
sets and Node.js for that account and writes its Claude configuration, then says so — you do not
have to find MCP Setup on each account. It needs GitHub access and the *Update server and
commands automatically* option (on by default); with either missing it says the connector is not
set up for that user yet and retries at the next Revit start.

If an account's configuration already holds a `revit-mcp` entry pointing at **another** user's
profile — a copied `claude_desktop_config.json`, a roaming profile, a machine that was cloned —
the plugin rewrites `command` and `args` to that account's own paths at the next Revit start.
Everything else in the entry, and every other connector, is left alone.

The **File** line in MCP Setup shows the full path being written, including the user name, so on
a shared machine you can see at a glance which profile is configured.

### What the plugin guarantees about your configuration file

Your other connectors are never affected. Concretely, when the plugin writes
`claude_desktop_config.json`:

- it reads the existing file and writes it back whole — every other entry in `mcpServers`
  (`Revit Connector`, `onenote`, …) and every other top-level setting (`preferences`,
  `coworkUserFilesPath`, …) is kept exactly as it was;
- only the `revit-mcp` entry is changed, and it is **merged**, not replaced: `command`, `args`
  and `REVIT_MCP_PORT` are set, anything else you put inside that entry stays;
- an entry already spelled differently (`Revit-MCP`) is updated in place instead of a second,
  lower-case entry being added next to it;
- a file that exists but is **not valid JSON is never overwritten** — the connectors in it
  cannot be read, so replacing it would delete them. MCP Setup reports the parse error and
  leaves the file alone (a dated `.unreadable-*.bak` copy is made so you can repair it);
- the new content is written to a temporary file in the same folder and swapped in one step,
  after a `.bak` copy of the original; a write that is blocked by a running client is retried;
- the result is read back before it is accepted: the `revit-mcp` entry must be there **and**
  every connector that was there before must still be there, otherwise the `.bak` is restored
  and MCP Setup reports the failure.

MCP Setup shows the full path of the file it writes on the **File** line, and **Show config
file** opens it in Explorer.

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
| `modify_element`, `operate_element` | same | Change parameters; select/hide/isolate/color |
| `delete_element` | delete_element | Delete elements by id; reports what was deleted and what was skipped |
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
- **Claude configuration**: it is written after every install/update and re-checked at every
  Revit start, so a moved user profile, a new Node.js runtime or a changed port cannot leave
  Claude pointing at nothing. Only the `revit-mcp` entry is ever touched.
- Manual: **MCP Setup ▸ Install / Update from GitHub** (stop the MCP server first — Revit
  locks loaded DLLs).

Publishing an update therefore only requires a new tag on the `revit-mcp` repository; see its
README for the release workflow.

## 7. Files and folders

```
%LOCALAPPDATA%\APGRevitPlugins\RevitMCP\
  server\                      MCP server (build\index.js, node_modules, package.json)
  server\runtime\node.exe      Node.js shipped with the server release
  runtime\node.exe             Node.js downloaded by the plugin (only if the release had none)
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

**`claude_desktop_config.json` does not contain `revit-mcp` after Configure Claude**
- Claude Desktop was running: it rewrites the file from memory whenever one of its settings
  changes and drops what was added from outside. Use **Configure Claude ▸ Restart Claude Desktop
  and configure**, or quit Claude completely and press **Configure Claude** again.
- Check the **File** line in MCP Setup: it shows the exact file being written. If your Claude
  profile lives under another Windows account than the one running Revit, that is the file the
  plugin writes and Claude reads another one. **Show config file** opens it.
- If the message says the file *is not valid JSON*, the plugin deliberately left it untouched
  rather than lose your other connectors. Repair the JSON (a dated `.unreadable-*.bak` copy is
  next to it) and press **Configure Claude** again.
- The plugin never removes your other connectors — it restores the `.bak` copy rather than
  write a file that lost one.

**ChatGPT does not see the Revit tools**
- ChatGPT is not configured by the plugin: click **Copy config JSON** in MCP Setup and paste the
  entry into ChatGPT's MCP/connector configuration, then restart it.
- The Revit side is the same for every client: **MCP Server** must be switched ON in Revit.

**Claude says the Revit tools are unavailable / no hammer icon**
- Claude Desktop must be fully restarted after the configuration was written (tray icon ▸ Quit,
  or the **Restart Claude Desktop** button).
- Check the **Claude** line in MCP Setup: it says *configured* only when the entry in the file
  matches this installation, and the **File** line shows which file that is.
- Check **MCP Setup ▸ Node.js**: it must show a runtime (normally *bundled with the MCP server*).
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
