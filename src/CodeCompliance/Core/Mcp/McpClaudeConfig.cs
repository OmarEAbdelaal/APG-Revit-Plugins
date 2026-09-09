using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeCompliance.Core.Mcp
{
    /// <summary>State of the revit-mcp entry in an AI client configuration file.</summary>
    public enum ClaudeConfigState
    {
        /// <summary>The configuration file does not exist (client not installed / never started).</summary>
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

    /// <summary>One configuration file the plugin can write.</summary>
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
        /// <summary>True when at least one file could not be written; the caller must not report success.</summary>
        public bool AnyFailed => Results.Any(r => r.Failed);
        public string Summary { get; set; } = "";
    }

    /// <summary>What <see cref="McpClaudeConfig.Apply"/> did to one configuration file.</summary>
    public sealed class ClaudeConfigResult
    {
        public string Target { get; set; } = "";
        /// <summary>Full path of the file, so the user can check it themselves.</summary>
        public string Path { get; set; } = "";
        public bool Changed { get; set; }
        public bool Skipped { get; set; }
        /// <summary>The file was left untouched because writing it was impossible or unsafe.</summary>
        public bool Failed { get; set; }
        public string Message { get; set; } = "";
        public List<string> PreservedServers { get; } = new List<string>();
    }

    /// <summary>
    /// Reads and writes the MCP configuration of Claude Desktop
    /// (<c>%APPDATA%\Claude\claude_desktop_config.json</c>) and, when it exists, of Claude Code
    /// (<c>%USERPROFILE%\.claude.json</c>). ChatGPT and any other MCP client take the same
    /// entry, which <see cref="Snippet"/> produces for copying by hand.
    ///
    /// Rules that must never be broken:
    /// <list type="bullet">
    /// <item>only the <c>revit-mcp</c> entry inside <c>mcpServers</c> is touched — every other
    /// connector and every other top-level setting is written back exactly as found. The entry
    /// is <i>merged</i>, not replaced, so keys the client or the user added inside it survive,
    /// and the key keeps whatever spelling it already had (<c>Revit-MCP</c> is not duplicated
    /// as a second <c>revit-mcp</c>);</item>
    /// <item>a file that exists but cannot be parsed is never overwritten: the other connectors
    /// in it are invisible to us and would be destroyed, so the write is refused instead;</item>
    /// <item>the new content goes to a temporary file that replaces the original in one step,
    /// after a .bak copy; the result is read back and every connector that was there before
    /// must still be there, otherwise the backup is restored.</item>
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

        /// <summary>JSON files are written as UTF-8 without a byte order mark: clients trip over a BOM.</summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

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

            if (!(config["mcpServers"] is JObject servers) || !(FindServerEntry(servers)?.Value is JObject entry))
                return ClaudeConfigState.NotConfigured;

            details = entry.Value<string>("command") + " " +
                      string.Join(" ", entry["args"]?.Select(a => a.ToString()) ?? Enumerable.Empty<string>());
            return Matches(entry, settings)
                ? ClaudeConfigState.Configured
                : ClaudeConfigState.PointsElsewhere;
        }

        /// <summary>The entry as JSON, for pasting into ChatGPT or any other MCP client.</summary>
        public static string Snippet(McpSettings settings)
        {
            return new JObject { ["mcpServers"] = new JObject { [ServerKey] = BuildEntry(settings) } }
                .ToString(Formatting.Indented);
        }

        /// <summary>
        /// The revit-mcp entry this installation needs. When <paramref name="existing"/> is given
        /// the plugin only overwrites what it owns — command, args and REVIT_MCP_PORT — so extra
        /// environment variables or client-specific keys inside that entry are kept.
        /// </summary>
        public static JObject BuildEntry(McpSettings settings, JObject? existing = null)
        {
            NodeInfo node = McpNode.Resolve();
            JObject entry = existing != null ? (JObject)existing.DeepClone() : new JObject();
            entry["command"] = node.Path ?? "node";
            entry["args"] = new JArray(McpPaths.ServerEntry);
            if (!(entry["env"] is JObject env))
            {
                env = new JObject();
                entry["env"] = env;
            }
            env["REVIT_MCP_PORT"] = settings.Port.ToString();
            return entry;
        }

        /// <summary>True when the fields the plugin owns already hold the right values.</summary>
        private static bool Matches(JObject entry, McpSettings settings)
        {
            // BuildEntry(entry) is "entry with our fields corrected": equal means nothing to correct.
            return JToken.DeepEquals(entry, BuildEntry(settings, entry));
        }

        /// <summary>
        /// The revit-mcp property whatever case it was written in. Claude does not care about the
        /// case of the key, so writing a second entry with our own spelling would give the user
        /// two Revit connectors instead of one.
        /// </summary>
        private static JProperty? FindServerEntry(JObject servers)
        {
            return servers.Properties()
                .FirstOrDefault(p => string.Equals(p.Name, ServerKey, StringComparison.OrdinalIgnoreCase));
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

            string restartMessage = "";
            if (outcome.ClaudeWasRunning && restartMode == ClaudeRestartMode.RestartIfRunning)
            {
                restartMessage = StartClaudeDesktop(exePath);
                // Claude Desktop needs a few seconds to show up in the process list; asking
                // straight after Process.Start always answered "not running" and produced a
                // warning about a change that had in fact been applied.
                outcome.ClaudeRestarted = WaitForClaudeDesktop(TimeSpan.FromSeconds(12));
                if (!outcome.ClaudeRestarted)
                    restartMessage = "Claude Desktop was closed - start it again to load the Revit tools.";
            }

            outcome.AtRiskOfRevert = outcome.ClaudeWasRunning && !outcome.ClaudeRestarted && outcome.AnyChanged;

            string written = string.Join("  ", outcome.Results.Where(r => !r.Skipped).Select(r => r.Message));
            if (outcome.Results.All(r => r.Skipped))
                written = "No Claude configuration file was found on this computer.";
            outcome.Summary = (written + "  " + restartMessage).Trim();
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
            var result = new ClaudeConfigResult { Target = target.Name, Path = target.Path };
            try
            {
                if (!target.Exists && !target.CreateIfMissing)
                {
                    result.Skipped = true;
                    result.Message = target.Name + " is not installed on this computer (no " + Path.GetFileName(target.Path) + ") - skipped.";
                    return result;
                }

                JObject config;
                if (target.Exists)
                {
                    try
                    {
                        config = ReadJson(target.Path);
                    }
                    catch (Exception ex)
                    {
                        // The connectors this file holds are invisible to us while it does not
                        // parse, so replacing it would silently delete them. Refuse instead.
                        string aside = target.Path + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak";
                        TryCopy(target.Path, aside);
                        McpLog.Error("Unreadable " + target.Name + " configuration, copied to " + aside, ex);
                        result.Failed = true;
                        result.Message = target.Name + ": " + target.Path + " is not valid JSON (" + ex.Message +
                                         "), so it was left untouched - overwriting it would have deleted the other " +
                                         "connectors in it. A copy is at " + Path.GetFileName(aside) +
                                         ". Repair the file (or delete it) and configure again.";
                        return result;
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
                    config = new JObject();
                }

                if (!(config["mcpServers"] is JObject servers))
                {
                    servers = new JObject();
                    config["mcpServers"] = servers;
                }

                JProperty? existingProperty = FindServerEntry(servers);
                string key = existingProperty?.Name ?? ServerKey;
                JObject? existingEntry = existingProperty?.Value as JObject;

                result.PreservedServers.AddRange(servers.Properties()
                    .Select(p => p.Name)
                    .Where(n => !string.Equals(n, key, StringComparison.Ordinal)));

                JObject desired = BuildEntry(settings, existingEntry);
                bool alreadyCorrect = existingEntry != null && JToken.DeepEquals(existingEntry, desired);
                if (alreadyCorrect && !force)
                {
                    result.Message = target.Name + " already points at this MCP server.";
                    return result;
                }

                bool wasPresent = existingEntry != null;
                servers[key] = desired;

                if (target.Exists)
                    TryCopy(target.Path, target.Path + ".bak");
                WriteAtomic(target.Path, config.ToString(Formatting.Indented));

                // Read back and compare: proves the file on disk really holds the entry, and
                // that nothing else was lost on the way.
                JObject verify = ReadJson(target.Path);
                JObject? verifyServers = verify["mcpServers"] as JObject;
                JObject? written = verifyServers == null ? null : FindServerEntry(verifyServers)?.Value as JObject;
                if (written == null || !JToken.DeepEquals(written, desired))
                    throw new IOException("the file was written but does not contain the expected entry");

                List<string> lost = result.PreservedServers
                    .Where(n => verifyServers!.Property(n) == null)
                    .ToList();
                if (lost.Count > 0)
                {
                    if (File.Exists(target.Path + ".bak"))
                        File.Copy(target.Path + ".bak", target.Path, true);
                    throw new IOException("the write would have removed other connectors (" +
                                          string.Join(", ", lost) + "); the previous file was restored");
                }

                int preserved = result.PreservedServers.Count;
                result.Changed = !alreadyCorrect;
                result.Message = target.Name + ": " + key + " " + (wasPresent ? "updated" : "added") +
                                 " in " + target.Path +
                                 (preserved > 0
                                     ? "; " + preserved + " other connector" + (preserved == 1 ? "" : "s") + " kept (" +
                                       string.Join(", ", result.PreservedServers) + ")"
                                     : "");
                McpLog.Info(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                McpLog.Error("Could not configure " + target.Name, ex);
                result.Failed = true;
                result.Message = target.Name + ": could not write " + target.Path + " - " + ex.Message;
                return result;
            }
        }

        private static JObject ReadJson(string path)
        {
            string text = File.ReadAllText(path);
            return text.Trim().Length == 0 ? new JObject() : JObject.Parse(text);
        }

        /// <summary>
        /// Writes through a temporary file in the same folder and swaps it in as one operation,
        /// so a failure half-way leaves the original configuration intact. Claude Desktop and
        /// Claude Code hold the file open for short moments, hence the retries.
        /// </summary>
        private static void WriteAtomic(string path, string text)
        {
            string temp = path + ".apg-tmp";
            Exception? last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    File.WriteAllText(temp, text, Utf8NoBom);
                    if (File.Exists(path))
                        File.Replace(temp, path, null, true);
                    else
                        File.Move(temp, path);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    last = ex;
                    TryDelete(temp);
                    System.Threading.Thread.Sleep(250 * (attempt + 1));
                }
            }
            throw last ?? new IOException("could not write " + path);
        }

        private static void TryCopy(string from, string to)
        {
            try
            {
                File.Copy(from, to, true);
            }
            catch (Exception ex)
            {
                McpLog.Error("Could not back up " + from, ex);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // nothing to do
            }
        }

        // ── Restarting Claude Desktop ──────────────────────────────────────────

        public static bool IsClaudeDesktopRunning() => GetClaudeProcesses().Count > 0;

        /// <summary>Waits for Claude Desktop to appear in the process list after starting it.</summary>
        private static bool WaitForClaudeDesktop(TimeSpan timeout)
        {
            DateTime until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (IsClaudeDesktopRunning())
                    return true;
                System.Threading.Thread.Sleep(500);
            }
            return false;
        }

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
                string message = StartClaudeDesktop(exePath);
                return WaitForClaudeDesktop(TimeSpan.FromSeconds(12))
                    ? message
                    : "Claude Desktop was closed - start it again to load the Revit tools.";
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
