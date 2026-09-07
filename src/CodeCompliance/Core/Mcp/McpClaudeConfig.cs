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
        /// </summary>
        public static List<ClaudeConfigResult> Apply(McpSettings settings, bool force = true)
        {
            var results = new List<ClaudeConfigResult>();
            foreach (ClaudeConfigTarget target in AllTargets())
                results.Add(ApplyTo(target, settings, force));
            return results;
        }

        /// <summary>Writes the entry only where it is missing or out of date (used after install and at startup).</summary>
        public static List<ClaudeConfigResult> EnsureConfigured(McpSettings settings)
        {
            return Apply(settings, force: false);
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

        public static bool IsClaudeDesktopRunning() => GetClaudeProcesses().Length > 0;

        private static Process[] GetClaudeProcesses()
        {
            try
            {
                return Process.GetProcessesByName("claude");
            }
            catch
            {
                return new Process[0];
            }
        }

        /// <summary>
        /// Closes Claude Desktop and starts it again so it re-reads its configuration.
        /// Returns a message for the user; never throws.
        /// </summary>
        public static string RestartClaudeDesktop()
        {
            try
            {
                Process[] running = GetClaudeProcesses();
                string? exePath = null;
                foreach (Process p in running)
                {
                    try
                    {
                        exePath ??= p.MainModule?.FileName;
                    }
                    catch
                    {
                        // access denied for some processes; keep looking
                    }
                }

                if (running.Length == 0)
                    return "Claude Desktop is not running. Start it to pick up the new configuration.";

                foreach (Process p in running)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch
                    {
                        // already gone
                    }
                }

                if (exePath != null && File.Exists(exePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                        return "Claude Desktop was restarted; the Revit tools load with it.";
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
                        return "Claude Desktop was restarted; the Revit tools load with it.";
                    }
                    catch
                    {
                        // fall through to the manual message
                    }
                }

                return "Claude Desktop was closed. Start it again to load the Revit tools.";
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
