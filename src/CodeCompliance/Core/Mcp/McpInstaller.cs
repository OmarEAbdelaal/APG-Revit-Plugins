using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeCompliance.Core.Mcp
{
    /// <summary>A published release of the Revit MCP connector on GitHub.</summary>
    public sealed class McpReleaseInfo
    {
        public Version Version { get; set; } = new Version(0, 0, 0);
        public string Tag { get; set; } = "";
        public string ReleaseUrl { get; set; } = McpInstaller.ReleasesUrl;
        public string? ServerZipUrl { get; set; }
        public string? CommandsZipUrl { get; set; }
        public bool IsComplete => ServerZipUrl != null && CommandsZipUrl != null;
    }

    /// <summary>What is installed locally (installed.json).</summary>
    public sealed class McpInstalledInfo
    {
        [JsonProperty("serverVersion")] public string? ServerVersion { get; set; }
        [JsonProperty("commandsVersion")] public string? CommandsVersion { get; set; }
        [JsonProperty("sourceTag")] public string? SourceTag { get; set; }
        [JsonProperty("installedUtc")] public DateTime? InstalledUtc { get; set; }

        [JsonIgnore] public bool Any => ServerVersion != null || CommandsVersion != null;

        [JsonIgnore]
        public Version? Version =>
            McpInstaller.ParseVersion(ServerVersion) ?? McpInstaller.ParseVersion(CommandsVersion);
    }

    public enum McpUpdateStatus { NotInstalled, UpToDate, Updated, Failed, Disabled }

    public sealed class McpUpdateResult
    {
        public McpUpdateStatus Status { get; set; }
        public string Message { get; set; } = "";
        public Version? NewVersion { get; set; }
        /// <summary>True when the Claude configuration was written as part of this update.</summary>
        public bool ClaudeConfigured { get; set; }
        /// <summary>True when Claude Desktop was running and can overwrite what was written.</summary>
        public bool ClaudeMustRestart { get; set; }
        /// <summary>
        /// True when this was the first setup for the Windows account running Revit, rather than
        /// an update of an existing one. Everything the connector needs lives under the user's own
        /// profile, so each account on a shared computer provisions itself the first time.
        /// </summary>
        public bool FirstRun { get; set; }
    }

    /// <summary>
    /// Installs and updates the Revit MCP connector from the GitHub releases of
    /// OmarEAbdelaal/revit-mcp: the MCP server (Node.js, with its own runtime) and the Revit
    /// command sets. Node.js handling lives in <see cref="McpNode"/>, the Claude configuration
    /// in <see cref="McpClaudeConfig"/>. No Revit API types here, so every method can run on a
    /// background thread.
    /// </summary>
    public static class McpInstaller
    {
        /// <summary>Said when the connector has never been set up for the current Windows account.</summary>
        private static string NotSetUpForThisUser =>
            "Revit MCP is not set up yet for the Windows user \"" + Environment.UserName + "\".";

        public const string Repo = "OmarEAbdelaal/revit-mcp";
        public const string RepoUrl = "https://github.com/" + Repo;
        public const string ReleasesUrl = RepoUrl + "/releases";
        private const string LatestReleaseApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
        private const string ServerAssetPrefix = "revit-mcp-server-";
        private const string CommandsAssetPrefix = "revit-mcp-commands-";
        private const string UserAgent = "APG-Revit-Plugins-RevitMCP";

        // ── Local state ────────────────────────────────────────────────────────

        public static bool IsServerInstalled => File.Exists(McpPaths.ServerEntry);

        public static bool IsCommandsInstalled =>
            Directory.Exists(McpPaths.CommandsDir) &&
            Directory.GetDirectories(McpPaths.CommandsDir).Any(d => File.Exists(Path.Combine(d, "command.json")));

        public static McpInstalledInfo ReadInstalled()
        {
            try
            {
                if (File.Exists(McpPaths.InstalledFile))
                    return JsonConvert.DeserializeObject<McpInstalledInfo>(File.ReadAllText(McpPaths.InstalledFile)) ?? new McpInstalledInfo();
            }
            catch (Exception ex)
            {
                McpLog.Error("Cannot read installed.json", ex);
            }
            var info = new McpInstalledInfo();
            // Fall back to the server package.json when installed.json is missing
            if (IsServerInstalled)
                info.ServerVersion = ReadServerPackageVersion();
            return info;
        }

        private static string? ReadServerPackageVersion()
        {
            try
            {
                if (!File.Exists(McpPaths.ServerPackageJson))
                    return null;
                return JObject.Parse(File.ReadAllText(McpPaths.ServerPackageJson)).Value<string>("version");
            }
            catch
            {
                return null;
            }
        }

        private static void WriteInstalled(McpInstalledInfo info)
        {
            McpPaths.EnsureDirectories();
            File.WriteAllText(McpPaths.InstalledFile, JsonConvert.SerializeObject(info, Formatting.Indented));
        }

        public static Version? ParseVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            Match m = Regex.Match(text, "([0-9]+)\\.([0-9]+)(?:\\.([0-9]+))?");
            if (!m.Success)
                return null;
            int major = int.Parse(m.Groups[1].Value);
            int minor = int.Parse(m.Groups[2].Value);
            int build = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            return new Version(major, minor, build);
        }

        // ── GitHub ─────────────────────────────────────────────────────────────

        private static HttpClient CreateClient(int timeoutSeconds)
        {
#if REVIT2024
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>Latest release with its two zip assets, or null when GitHub cannot be reached.</summary>
        public static async Task<McpReleaseInfo?> GetLatestReleaseAsync()
        {
            try
            {
                using (HttpClient client = CreateClient(20))
                {
                    string json = await client.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);
                    JObject release = JObject.Parse(json);
                    var info = new McpReleaseInfo
                    {
                        Tag = release.Value<string>("tag_name") ?? "",
                        ReleaseUrl = release.Value<string>("html_url") ?? ReleasesUrl
                    };
                    info.Version = ParseVersion(info.Tag) ?? new Version(0, 0, 0);
                    foreach (JObject asset in release["assets"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    {
                        string name = asset.Value<string>("name") ?? "";
                        string? url = asset.Value<string>("browser_download_url");
                        if (name.StartsWith(ServerAssetPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            info.ServerZipUrl = url;
                        else if (name.StartsWith(CommandsAssetPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            info.CommandsZipUrl = url;
                    }
                    return info;
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn("GitHub release check failed: " + ex.Message);
                return null;
            }
        }

        // ── Install / update ───────────────────────────────────────────────────

        /// <summary>
        /// Downloads and installs the server and the command sets of a release, makes sure a
        /// Node.js runtime is available and writes the Claude configuration.
        /// Command DLLs already loaded by a running MCP server cannot be replaced: stop the
        /// server (or restart Revit) first. Throws with a readable message on failure.
        /// </summary>
        public static async Task<string> InstallAsync(McpReleaseInfo release, IProgress<string>? progress = null)
        {
            if (!release.IsComplete)
                throw new InvalidOperationException("Release " + release.Tag + " has no server/commands zip assets. " +
                                                    "Check " + release.ReleaseUrl);
            McpPaths.EnsureDirectories();
            Directory.CreateDirectory(McpPaths.TempDir);
            var installed = ReadInstalled();

            using (HttpClient client = CreateClient(900))
            {
                progress?.Report("Downloading MCP server " + release.Tag + " ...");
                string serverZip = await DownloadAsync(client, release.ServerZipUrl!, "server.zip").ConfigureAwait(false);
                progress?.Report("Downloading Revit command sets " + release.Tag + " ...");
                string commandsZip = await DownloadAsync(client, release.CommandsZipUrl!, "commands.zip").ConfigureAwait(false);

                progress?.Report("Installing MCP server ...");
                InstallServer(serverZip);
                installed.ServerVersion = ReadServerPackageVersion() ?? release.Version.ToString(3);

                progress?.Report("Installing Revit command sets ...");
                int sets = InstallCommands(commandsZip);
                installed.CommandsVersion = release.Version.ToString(3);
                installed.SourceTag = release.Tag;
                installed.InstalledUtc = DateTime.UtcNow;
                WriteInstalled(installed);

                TryDelete(serverZip);
                TryDelete(commandsZip);

                // The server release normally carries its own Node.js; download one if it does not.
                NodeInfo node = await McpNode.EnsureAsync(progress).ConfigureAwait(false);

                // Point Claude at what was just installed, keeping every other connector.
                progress?.Report("Updating the Claude configuration ...");
                ClaudeApplyOutcome claude = McpClaudeConfig.Apply(McpSettings.Load());

                string summary = "Installed Revit MCP " + release.Tag + " (server " + installed.ServerVersion +
                                 ", " + sets + " command set(s), Node.js " + (node.Version ?? "not found") + "). " +
                                 claude.Summary;
                McpLog.Info(summary);
                return summary;
            }
        }

        private static async Task<string> DownloadAsync(HttpClient client, string url, string fileName)
        {
            string target = Path.Combine(McpPaths.TempDir, fileName);
            using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (FileStream file = File.Create(target))
                {
                    await source.CopyToAsync(file).ConfigureAwait(false);
                }
            }
            return target;
        }

        /// <summary>Extracts the server zip into a fresh folder and swaps it in (JS files are not locked by Node).</summary>
        private static void InstallServer(string zipPath)
        {
            string staging = Path.Combine(McpPaths.TempDir, "server-" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(zipPath, staging);
            string root = FindRoot(staging, dir => File.Exists(Path.Combine(dir, "build", "index.js")))
                          ?? throw new InvalidOperationException("The server zip does not contain build\\index.js");

            if (Directory.Exists(McpPaths.ServerDir))
            {
                try
                {
                    Directory.Delete(McpPaths.ServerDir, true);
                }
                catch (Exception)
                {
                    // Something holds a file open (Claude may be running the server): move it aside
                    string old = McpPaths.ServerDir + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    Directory.Move(McpPaths.ServerDir, old);
                }
            }
            Directory.Move(root, McpPaths.ServerDir);
            TryDeleteDirectory(staging);
            CleanOldServerFolders();
        }

        /// <summary>Removes server.old-* folders left behind when a running server locked files.</summary>
        private static void CleanOldServerFolders()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(McpPaths.Root, "server.old-*"))
                    TryDeleteDirectory(dir);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>Extracts the command sets zip and replaces each set folder under Commands\.</summary>
        private static int InstallCommands(string zipPath)
        {
            string staging = Path.Combine(McpPaths.TempDir, "commands-" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(zipPath, staging);
            string root = FindRoot(staging, dir => Directory.GetDirectories(dir).Any(d => File.Exists(Path.Combine(d, "command.json"))))
                          ?? throw new InvalidOperationException("The commands zip contains no command set (folder with command.json)");

            int count = 0;
            foreach (string setDir in Directory.GetDirectories(root))
            {
                if (!File.Exists(Path.Combine(setDir, "command.json")))
                    continue;
                string target = Path.Combine(McpPaths.CommandsDir, Path.GetFileName(setDir));
                if (Directory.Exists(target))
                {
                    try
                    {
                        Directory.Delete(target, true);
                    }
                    catch (Exception ex)
                    {
                        throw new IOException("Cannot replace " + target + " - the command DLLs are in use. " +
                                              "Switch the MCP server off (or restart Revit) and try again. " + ex.Message);
                    }
                }
                Directory.Move(setDir, target);
                count++;
            }
            TryDeleteDirectory(staging);
            return count;
        }

        /// <summary>Zips may wrap their content in one top-level folder; find the folder that matches.</summary>
        private static string? FindRoot(string dir, Func<string, bool> matches, int depth = 0)
        {
            if (matches(dir))
                return dir;
            if (depth >= 2)
                return null;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string? found = FindRoot(sub, matches, depth + 1);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// Startup check: installs a newer release when one exists, and makes sure the Claude
        /// configuration points at this installation (Claude picks the change up on its next start).
        /// Never throws. Command updates take effect the next time the MCP server switch is used.
        ///
        /// <para>Everything the connector needs — the server, the command sets, the Node.js runtime,
        /// the settings and the Claude configuration — lives under the profile of the Windows
        /// account running Revit. On a computer with several accounts the second account therefore
        /// starts with nothing, so when nothing is installed for it this performs the first install
        /// itself instead of waiting for the user to find MCP Setup. Every path written into the
        /// Claude configuration is resolved at that moment from this account's own profile, so no
        /// other user's folder can end up in it.</para>
        /// </summary>
        public static async Task<McpUpdateResult> AutoUpdateAsync(McpSettings settings)
        {
            var result = new McpUpdateResult();
            try
            {
                McpInstalledInfo installed = ReadInstalled();
                result.FirstRun = !installed.Any;

                // Keep Claude pointing at this installation even when nothing needs updating:
                // a moved profile, another account's path, a new Node.js or a changed port would
                // otherwise break it. Skipped on a first run: there is nothing to point at yet.
                if (!result.FirstRun)
                {
                    try
                    {
                        ClaudeApplyOutcome claude = McpClaudeConfig.EnsureConfigured(settings);
                        result.ClaudeConfigured = claude.AnyChanged;
                        result.ClaudeMustRestart = claude.AtRiskOfRevert;
                    }
                    catch (Exception ex)
                    {
                        McpLog.Error("Claude configuration check failed", ex);
                    }
                }

                if (!settings.AutoUpdate)
                {
                    result.Status = result.FirstRun ? McpUpdateStatus.NotInstalled : McpUpdateStatus.Disabled;
                    result.Message = result.FirstRun
                        ? NotSetUpForThisUser + " Automatic setup is switched off, so use MCP Setup on the APG Revit Plugins tab."
                        : "Automatic updates are switched off.";
                    return result;
                }

                McpReleaseInfo? latest = await GetLatestReleaseAsync().ConfigureAwait(false);
                if (latest == null || !latest.IsComplete)
                {
                    result.Status = result.FirstRun ? McpUpdateStatus.NotInstalled : McpUpdateStatus.Failed;
                    result.Message = result.FirstRun
                        ? NotSetUpForThisUser + " GitHub could not be reached to set it up now - it is retried the next time Revit starts."
                        : "Could not check GitHub for updates.";
                    return result;
                }
                if (!result.FirstRun)
                {
                    Version current = installed.Version ?? new Version(0, 0, 0);
                    if (latest.Version <= current)
                    {
                        result.Status = McpUpdateStatus.UpToDate;
                        result.Message = "Revit MCP " + current.ToString(3) + " is up to date.";
                        return result;
                    }
                }
                if (McpSocketService.Instance.IsRunning)
                {
                    result.Status = McpUpdateStatus.Failed;
                    result.Message = "Update " + latest.Tag + " is available but the MCP server is running. Switch it off and open MCP Setup to update.";
                    return result;
                }

                // InstallAsync writes the Claude configuration itself, with the paths of the
                // account running Revit right now.
                result.Message = await InstallAsync(latest).ConfigureAwait(false);
                result.Status = McpUpdateStatus.Updated;
                result.NewVersion = latest.Version;
                result.ClaudeConfigured = true;
                return result;
            }
            catch (Exception ex)
            {
                McpLog.Error("Auto-update failed", ex);
                result.Status = McpUpdateStatus.Failed;
                result.Message = ex.Message;
                return result;
            }
        }

        // ── Convenience wrappers used by the UI ────────────────────────────────

        public static NodeInfo GetNode() => McpNode.Resolve();

        public static Task<NodeInfo> EnsureNodeAsync(IProgress<string>? progress = null) => McpNode.EnsureAsync(progress);

        public static string ClaudeConfigSnippet(McpSettings settings) => McpClaudeConfig.Snippet(settings);

        // ── helpers ────────────────────────────────────────────────────────────

        private static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch
            {
                // ignore
            }
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // ignore
            }
        }
    }
}
