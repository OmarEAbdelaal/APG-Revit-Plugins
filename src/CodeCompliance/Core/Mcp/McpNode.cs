using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodeCompliance.Core.Mcp
{
    /// <summary>Where the Node.js used to run the MCP server comes from.</summary>
    public enum NodeSource
    {
        None,
        /// <summary>runtime\node.exe shipped inside the server release (preferred).</summary>
        BundledWithServer,
        /// <summary>node.exe downloaded by this plugin from nodejs.org.</summary>
        DownloadedByPlugin,
        /// <summary>A Node.js installation found on the machine.</summary>
        SystemInstallation
    }

    /// <summary>The Node.js runtime the plugin will hand to the AI client.</summary>
    public sealed class NodeInfo
    {
        public NodeInfo(string? path, string? version, NodeSource source)
        {
            Path = path;
            Version = version;
            Source = source;
        }

        public string? Path { get; }
        public string? Version { get; }
        public NodeSource Source { get; }
        public bool Found => Path != null;
        public bool IsSupported => McpNode.IsVersionSupported(Version);

        public string Describe()
        {
            if (!Found)
                return "not found";
            string where = Source switch
            {
                NodeSource.BundledWithServer => "bundled with the MCP server",
                NodeSource.DownloadedByPlugin => "downloaded by the plugin",
                _ => "installed on this computer"
            };
            return (Version ?? "unknown version") + "  ·  " + where + "  ·  " + Path;
        }
    }

    /// <summary>
    /// Finds, and if necessary downloads, the Node.js runtime that runs the MCP server.
    ///
    /// Since server release v1.2.0 the runtime travels inside the server zip
    /// (server\runtime\node.exe), so nothing has to be installed on the user's machine.
    /// For older releases, or if that file is missing, the plugin downloads the official
    /// Windows build from nodejs.org into RevitMCP\runtime and verifies its SHA256.
    /// A Node.js already installed on the machine is used as the last option.
    ///
    /// No Revit API types here: everything may run on a background thread.
    /// </summary>
    public static class McpNode
    {
        public static readonly Version MinimumVersion = new Version(22, 13, 0);
        private const string DistIndexUrl = "https://nodejs.org/dist/index.json";
        private const string UserAgent = "APG-Revit-Plugins-RevitMCP";

        /// <summary>Node.js to use, in order: bundled with the server, downloaded, installed.</summary>
        public static NodeInfo Resolve()
        {
            if (File.Exists(McpPaths.BundledNodeExe))
                return new NodeInfo(McpPaths.BundledNodeExe, GetVersion(McpPaths.BundledNodeExe), NodeSource.BundledWithServer);

            if (File.Exists(McpPaths.DownloadedNodeExe))
                return new NodeInfo(McpPaths.DownloadedNodeExe, GetVersion(McpPaths.DownloadedNodeExe), NodeSource.DownloadedByPlugin);

            string? system = FindSystemNode();
            if (system != null)
                return new NodeInfo(system, GetVersion(system), NodeSource.SystemInstallation);

            return new NodeInfo(null, null, NodeSource.None);
        }

        /// <summary>Full path of an installed node.exe (PATH first, then the usual folders), or null.</summary>
        public static string? FindSystemNode()
        {
            var candidates = new List<string>();
            try
            {
                var psi = new ProcessStartInfo("where.exe", "node.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (Process? p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);
                        candidates.AddRange(output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                    }
                }
            }
            catch
            {
                // where.exe missing or blocked
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"));
            return candidates.Select(c => c.Trim()).FirstOrDefault(c => c.Length > 0 && File.Exists(c));
        }

        /// <summary>Output of "node --version" (for example v24.20.0), or null.</summary>
        public static string? GetVersion(string nodeExe)
        {
            try
            {
                var psi = new ProcessStartInfo(nodeExe, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (Process? p = Process.Start(psi))
                {
                    if (p == null)
                        return null;
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(5000);
                    return output.Length > 0 ? output : null;
                }
            }
            catch
            {
                return null;
            }
        }

        public static bool IsVersionSupported(string? version)
        {
            Version? v = McpInstaller.ParseVersion(version);
            return v != null && v >= MinimumVersion;
        }

        /// <summary>
        /// Makes sure a usable Node.js exists, downloading the latest LTS from nodejs.org when
        /// none is found (or when the one found is too old). Returns what will be used.
        /// </summary>
        public static async Task<NodeInfo> EnsureAsync(IProgress<string>? progress = null)
        {
            NodeInfo current = Resolve();
            if (current.Found && current.IsSupported)
                return current;

            progress?.Report(current.Found
                ? "Node.js " + current.Version + " is too old; downloading the current LTS ..."
                : "Node.js was not found; downloading it from nodejs.org ...");

            await DownloadLatestLtsAsync(progress).ConfigureAwait(false);
            return Resolve();
        }

        /// <summary>Downloads the latest Node.js LTS for Windows x64 into RevitMCP\runtime.</summary>
        public static async Task<string> DownloadLatestLtsAsync(IProgress<string>? progress = null)
        {
            McpPaths.EnsureDirectories();
            Directory.CreateDirectory(McpPaths.TempDir);

            using (HttpClient client = CreateClient())
            {
                string indexJson = await client.GetStringAsync(DistIndexUrl).ConfigureAwait(false);
                JArray releases = JArray.Parse(indexJson);
                JObject? lts = releases.OfType<JObject>()
                    .FirstOrDefault(r => r["lts"] != null && r["lts"]!.Type != JTokenType.Boolean);
                string? version = lts?.Value<string>("version");
                if (string.IsNullOrEmpty(version))
                    throw new InvalidOperationException("Could not determine the current Node.js LTS version from nodejs.org.");

                string name = "node-" + version + "-win-x64";
                string zipUrl = "https://nodejs.org/dist/" + version + "/" + name + ".zip";
                string zipPath = Path.Combine(McpPaths.TempDir, name + ".zip");

                progress?.Report("Downloading Node.js " + version + " (about 35 MB) ...");
                using (HttpResponseMessage response = await client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (FileStream file = File.Create(zipPath))
                    {
                        await source.CopyToAsync(file).ConfigureAwait(false);
                    }
                }

                // Verify against the checksum list published next to the download
                string sums = await client.GetStringAsync("https://nodejs.org/dist/" + version + "/SHASUMS256.txt").ConfigureAwait(false);
                string? expected = sums
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.EndsWith(name + ".zip", StringComparison.OrdinalIgnoreCase))
                    ?.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                string actual = Sha256(zipPath);
                if (expected == null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(zipPath);
                    throw new InvalidOperationException("The Node.js download did not match the checksum published by nodejs.org.");
                }

                progress?.Report("Installing Node.js " + version + " ...");
                Directory.CreateDirectory(McpPaths.DownloadedNodeDir);
                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "node.exe", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                        throw new InvalidOperationException("node.exe was not found inside the Node.js download.");
                    entry.ExtractToFile(McpPaths.DownloadedNodeExe, true);
                }
                File.WriteAllText(Path.Combine(McpPaths.DownloadedNodeDir, "node-version.txt"), version);
                TryDelete(zipPath);

                McpLog.Info("Node.js " + version + " installed at " + McpPaths.DownloadedNodeExe);
                return McpPaths.DownloadedNodeExe;
            }
        }

        private static HttpClient CreateClient()
        {
#if REVIT2024
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

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
    }
}
