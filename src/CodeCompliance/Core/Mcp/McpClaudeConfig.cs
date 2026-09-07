using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeCompliance.Core.Mcp
{
    /// <summary>State of the revit-mcp entry in a Claude configuration file.</summary>
    public enum ClaudeConfigState
    {
        /// <summary>The configuration file does not exist (Claude not installed / never started).</summary>
        FileMissing,
        /// <summary>The file exists but has no revit-mcp entry.</summary>
        NotConfigured,
        /// <summary>The entry exists and matches this installation.</summary>
        Configured,
        /// <summary>An entry exists but points at another server, node or port.</summary>
        PointsElsewhere,
        /// <summary>The file exists but is not valid JSON.</summary>
        Unreadable
    }

    /// <summary>One Claude configuration file the plugin can write.</summary>
    public sealed class ClaudeConfigTarget
    {
        public ClaudeConfigTarget(string name, string path, bool createIfMissing)
        {
            Name = name;
            Path = path;
            CreateIfMissing = createIfMissing;
        }

        public string Name { get; }
        public string Path { get; }
        /// <summary>Claude Desktop's file is created when missing; Claude Code's is only updated.</summary>
        public bool CreateIfMissing { get; }
        public bool Exists => File.Exists(Path);
    }

    /// <summary>Whether Claude Desktop may be closed to make the change stick.</summary>
    public enum ClaudeRestartMode
    {
        /// <summary>Write the file and leave Claude alone (it may revert the change later).</summary>
        Never,
        /// <summary>Close Claude Desktop, write, then start it again - the only reliable order.</summary>
        RestartIfRunning
    }

    /// <summary>Result of configuring every Claude installation on this machine.</summary>
    public sealed class ClaudeApplyOutcome
    {
        public List<ClaudeConfigResult> Results { get; } = new List<ClaudeConfigResult>();
        public bool ClaudeWasRunning { get; set; }
        public bool ClaudeRestarted { get; set; }
        /// <summary>True when Claude Desktop kept running: it can overwrite what was just written.</summary>
        public bool AtRiskOfRevert { get; set; }
        public bool AnyChanged => Results.Any(r => r.Changed);
        public string Summary { get; set; } = "";
    }

    /// <summary>What <see cref="McpClaudeConfig.Apply"/> did to one configuration file.</summary>
    public sealed class ClaudeConfigResult
    {
        public string Target { get; set; } = "";
        public bool Changed { get; set; }
        public bool Skipped { get; set; }
        public string Message { get; set; } = "";
        public List<string> PreservedServers { get; } = new List<string>();
    }

    /// <summary>
    /// Reads and writes the MCP configuration of Claude Desktop
    /// (<c>%APPDATA%\Claude\claude_desktop_config.json</c>) and, when it exists, of Claude Code
    /// (<c>%USERPROFILE%\.claude.json</c>).
    ///
    /// Rules that must never be broken:
    /// <list type="bullet">
    /// <item>only the <c>revit-mcp</c> entry inside <c>mcpServers</c> is touched — every other
    /// connector and every other top-level setting is written back exactly as found;</item>
    /// <item>a .bak copy is written before any change, and the file is read back and compared
    /// afterwards, so a half-written configuration is detected instead of silently accepted;</item>
    /// <item>an unreadable file is never overwritten silently: it is copied aside first.</item>
    /// </list>
    ///
    /// <para><b>Claude Desktop owns its file while it runs.</b> It reads the configuration when
    /// it starts and writes its in-memory copy back whenever one of its own settings changes,
    /// which silently discards edits made from outside meanwhile. Writing therefore only sticks
    /// when Claude Desktop is closed: <see cref="ClaudeRestartMode.RestartIfRunning"/> closes it,
    /// writes, and starts it again. When it is left running the result is flagged with
    /// <see cref="ClaudeApplyOutcome.AtRiskOfRevert"/> so the user can be told.</para>
    /// </summary>
    public static class McpClaudeConfig
    {
        /// <summary>Key of the server entry inside mcpServers.</summary>
        public const string ServerKey = "revit-mcp";

        public static ClaudeConfigTarget Desktop => new ClaudeConfigTarget("Claude Desktop", McpPaths.ClaudeConfigFile, true);
        public static ClaudeConfigTarget Code => new ClaudeConfigTarget("Claude Code", McpPaths.ClaudeCodeConfigFile, false);

        public static IEnumerable<ClaudeConfigTarget> AllTargets()
        {
            yield return Desktop;
            yield return Code;
        }

        // ── Reading ────────────────────────────────────────────────────────────

        /// <summary>State of one configuration file, with a short description of what is in it.</summary>
        public static ClaudeConfigState Inspect(ClaudeConfigTarget target, McpSettings settings, out string details)
        {
            details = target.Path;
            if (!target.Exists)
                return ClaudeConfigState.FileMissing;

            JObject config;
            try
            {
                config = ReadJson(target.Path);
            }
            catch (Exception ex)
            {
                details = "cannot read " + target.Path + ": " + ex.Message;
                return ClaudeConfigState.Unreadable;
            }

            if (!(config["mcpServers"] is JObject servers) || !(servers[ServerKey] is JObject entry))
                return ClaudeConfigState.NotConfigured;

            details = entry.Value<string>("command") + " " +
                      string.Join(" ", entry["args"]?.Select(a => a.ToString()) ?? Enumerable.Empty<string>());
            return JToken.DeepEquals(entry, BuildEntry(settings))
                ? ClaudeConfigState.Configured
                : ClaudeConfigState.PointsElsewhere;
        }

        /// <summary>The entry as JSON, for pasting into any other MCP client.</summary>
        public static string Snippet(McpSettings settings)
        {
            return new JObject { ["mcpServers"] = new JObject { [ServerKey] = BuildEntry(settings) } }
                .ToString(Formatting.Indented);
        }

        /// <summary>The revit-mcp entry this installation needs.</summary>
        public static JObject BuildEntry(McpSettings settings)
        {
            NodeInfo node = McpNode.Resolve();
            return new JObject
            {
                ["command"] = node.Path ?? "node",
                ["args"] = new JArray(McpPaths.ServerEntry),
                ["env"] = new JObject { ["REVIT_MCP_PORT"] = settings.Port.ToString() }
            };
        }

        // ── Writing ────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the revit-mcp entry into every Claude configuration this machine has,
        /// keeping all other servers and settings. Claude Desktop's file is created when
        /// missing; Claude Code's is only updated when it already exists.
        ///
        /// With <see cref="ClaudeRestartMode.RestartIfRunning"/> a running Claude Desktop is
        /// closed before writing and started again afterwards, because Claude overwrites the
        /// file from memory while it runs.
        /// </summary>
        public static ClaudeApplyOutcome Apply(
            McpSettings settings,
            ClaudeRestartMode restartMode = ClaudeRestartMode.Never,
            bool force = true)
        {
            var outcome = new ClaudeApplyOutcome();
            outcome.ClaudeWasRunning = IsClaudeDesktopRunning();

            string? exePath = null;
            if (outcome.ClaudeWasRunning && restartMode == ClaudeRestartMode.RestartIfRunning)
            {
                exePath = GetClaudeExecutablePath();
                CloseClaudeDesktop();
            }

            foreach (ClaudeConfigTarget target in AllTargets())
                outcome.Results.Add(ApplyTo(target, settings, force));

            if (outcome.ClaudeWasRunning && restartMode == ClaudeRestartMode.RestartIfRunning)
            {
                string startMessage = StartClaudeDesktop(exePath);
                outcome.ClaudeRestarted = !IsClaudeDesktopRunning() ? false : true;
                outcome.Summary = startMessage;
            }

            outcome.AtRiskOfRevert = outcome.ClaudeWasRunning && !outcome.ClaudeRestarted && outcome.AnyChanged;

            string written = string.Join("  ", outcome.Results.Where(r => !r.Skipped).Select(r => r.Message));
            if (outcome.Results.All(r => r.Skipped))
                written = "No Claude configuration file was found on this computer.";
            outcome.Summary = (written + "  " + outcome.Summary).Trim();
            if (outcome.AtRiskOfRevert)
                outcome.Summary += "  WARNING: Claude Desktop is running and rewrites this file from memory, " +
                                   "which undoes the change. Close Claude Desktop and configure again, or use the restart option.";
            return outcome;
        }

        /// <summary>Writes the entry only where it is missing or out of date (used after install and at startup).</summary>
        public static ClaudeApplyOutcome EnsureConfigured(McpSettings settings)
        {
            return Apply(settings, ClaudeRestartMode.Never, force: false);
        }

        private static ClaudeConfigResult ApplyTo(ClaudeConfigTarget target, McpSettings settings, bool force)
        {
            var result = new ClaudeConfigResult { Target = target.Name };
            try
            {
                if (!target.Exists && !target.CreateIfMissing)
                {
                    result.Skipped = true;
                    result.Message = target.Name + " is not installed on this computer (no " + Path.GetFileName(target.Path) + ") - skipped.";
                    return result;
                }

                JObject config = new JObject();
                if (target.Exists)
                {
                    try
                    {
                        config = ReadJson(target.Path);
                    }
                    catch (Exception ex)
                    {
                        // Never destroy something we could not understand: keep a dated copy.
                        string aside = target.Path + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak";
                        File.Copy(target.Path, aside, true);
                        McpLog.Error("Unreadable " + target.Name + " configuration, copied to " + aside, ex);
                        result.Message = target.Name + ": the existing configuration is not valid JSON. A copy was saved as " +
                                         Path.GetFileName(aside) + " and a fresh configuration was written.";
                        config = new JObject();
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
                }

                if (!(config["mcpServers"] is JObject servers))
                {
                    servers = new JObject();
                    config["mcpServers"] = servers;
                }

                result.PreservedServers.AddRange(servers.Properties()
                    .Select(p => p.Name)
                    .Where(n => !string.Equals(n, ServerKey, StringComparison.OrdinalIgnoreCase)));

                JObject desired = BuildEntry(settings);
                bool alreadyCorrect = servers[ServerKey] is JObject existing && JToken.DeepEquals(existing, desired);
                if (alreadyCorrect && !force)
                {
                    result.Message = target.Name + " already points at this MCP server.";
                    return result;
                }

                bool wasPresent = servers[ServerKey] != null;
                servers[ServerKey] = desired;

                if (target.Exists)
                    File.Copy(target.Path, target.Path + ".bak", true);
                File.WriteAllText(target.Path, config.ToString(Formatting.Indented));

                // Read back and compare: proves the file on disk really holds the entry.
                JObject verify = ReadJson(target.Path);
                JObject? written = (verify["mcpServers"] as JObject)?[ServerKey] as JObject;
                if (written == null || !JToken.DeepEquals(written, desired))
                    throw new IOException("the file was written but does not contain the expected entry");

                int preserved = ((JObject)verify["mcpServers"]!).Properties().Count() - 1;
                result.Changed = !alreadyCorrect;
                result.Message = target.Name + ": revit-mcp " + (wasPresent ? "updated" : "added") +
                                 " in " + Path.GetFileName(target.Path) +
                                 (preserved > 0
                                     ? "; " + preserved + " other connector" + (preserved == 1 ? "" : "s") + " kept (" +
                                       string.Join(", ", result.PreservedServers) + ")"
                                     : "");
                McpLog.Info(result.Message + " -> " + target.Path);
                return result;
            }
            catch (Exception ex)
            {
                McpLog.Error("Could not configure " + target.Name, ex);
                result.Message = target.Name + ": could not write the configuration - " + ex.Message;
                return result;
            }
        }

        private static JObject ReadJson(string path)
        {
            string text = File.ReadAllText(path);
            return text.Trim().Length == 0 ? new JObject() : JObject.Parse(text);
        }

        // ── Restarting Claude Desktop ──────────────────────────────────────────

        public static bool IsClaudeDesktopRunning() => GetClaudeProcesses().Count > 0;

        /// <summary>
        /// The Claude Desktop application processes. "claude.exe" is also the name of the
        /// Claude Code command line tool (%APPDATA%\Claude\claude-code\...), which must never
        /// be killed or started in place of the desktop app, so those are filtered out.
        /// </summary>
        private static List<Process> GetClaudeProcesses()
        {
            var result = new List<Process>();
            try
            {
                foreach (Process p in Process.GetProcessesByName("claude"))
                {
                    if (IsDesktopApp(p))
                        result.Add(p);
                }
            }
            catch
            {
                // process enumeration can fail under restricted rights
            }
            return result;
        }

        private static bool IsDesktopApp(Process process)
        {
            string? path = TryGetPath(process);
            if (path != null)
            {
                if (path.IndexOf("\\claude-code\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false; // Claude Code CLI
                if (path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    path.IndexOf("AnthropicClaude", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;  // Store or per-user install of the desktop app
            }
            try
            {
                // Fall back to "has a window": the CLI does not, the desktop app does.
                return process.MainWindowHandle != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private static string? TryGetPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Path of the running Claude Desktop executable, when it can be read.</summary>
        public static string? GetClaudeExecutablePath()
        {
            foreach (Process p in GetClaudeProcesses())
            {
                string? path = TryGetPath(p);
                if (path != null)
                    return path;
            }
            return null;
        }

        /// <summary>Ends every Claude Desktop process and waits for the file to be released.</summary>
        public static void CloseClaudeDesktop()
        {
            foreach (Process p in GetClaudeProcesses())
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(8000);
                }
                catch
                {
                    // already gone
                }
            }
            // Give the app a moment to finish flushing its own files before we write ours.
            System.Threading.Thread.Sleep(700);
        }

        /// <summary>Starts Claude Desktop again (by path, else through the Start menu entry).</summary>
        public static string StartClaudeDesktop(string? exePath)
        {
            if (exePath != null && File.Exists(exePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                    return "Claude Desktop was restarted; it loads the Revit tools now.";
                }
                catch
                {
                    // Store (MSIX) installs cannot always be started by path - fall through
                }
            }

            string? appsFolderId = GetStoreAppId(exePath);
            if (appsFolderId != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + appsFolderId) { UseShellExecute = true });
                    return "Claude Desktop was restarted; it loads the Revit tools now.";
                }
                catch
                {
                    // fall through to the manual message
                }
            }

            return "Claude Desktop was closed - start it again to load the Revit tools.";
        }

        /// <summary>
        /// Closes Claude Desktop and starts it again so it re-reads its configuration.
        /// Returns a message for the user; never throws.
        /// </summary>
        public static string RestartClaudeDesktop()
        {
            try
            {
                if (!IsClaudeDesktopRunning())
                    return "Claude Desktop is not running. Start it to pick up the configuration.";
                string? exePath = GetClaudeExecutablePath();
                CloseClaudeDesktop();
                return StartClaudeDesktop(exePath);
            }
            catch (Exception ex)
            {
                McpLog.Error("Restarting Claude Desktop failed", ex);
                return "Could not restart Claude Desktop automatically: " + ex.Message + ". Close and start it yourself.";
            }
        }

        /// <summary>Turns ...\WindowsApps\Claude_1.2.3_x64__abc\app\claude.exe into Claude_abc!Claude.</summary>
        private static string? GetStoreAppId(string? exePath)
        {
            if (exePath == null || exePath.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) < 0)
                return null;
            try
            {
                string[] parts = exePath.Split(Path.DirectorySeparatorChar);
                string? packageDir = parts.FirstOrDefault(p => p.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase));
                if (packageDir == null)
                    return null;
                string[] segments = packageDir.Split('_');
                if (segments.Length < 2)
                    return null;
                string familyName = segments[0] + "_" + segments[segments.Length - 1];
                return familyName + "!Claude";
            }
            catch
            {
                return null;
            }
        }
    }
}
