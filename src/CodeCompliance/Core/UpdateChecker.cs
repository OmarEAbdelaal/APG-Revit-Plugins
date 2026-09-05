using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CodeCompliance.Core
{
    /// <summary>Result of an update check against the GitHub releases feed.</summary>
    public sealed class UpdateInfo
    {
        public UpdateInfo(Version current, Version latest, string releaseUrl, string? installerUrl)
        {
            Current = current;
            Latest = latest;
            ReleaseUrl = releaseUrl;
            InstallerUrl = installerUrl;
        }

        public Version Current { get; }
        public Version Latest { get; }
        public string ReleaseUrl { get; }
        public string? InstallerUrl { get; }
        public bool IsNewer => Latest > Current;

        /// <summary>Best link for the user: the installer itself, else the release page.</summary>
        public string DownloadUrl => InstallerUrl ?? ReleaseUrl;
    }

    /// <summary>
    /// Checks the public GitHub releases feed of the suite for a newer version.
    /// No Revit API types here; safe to call from a background task. All failures
    /// (offline, rate limit, changed feed) return null — updating is never allowed
    /// to disturb normal plugin use.
    /// </summary>
    public static class UpdateChecker
    {
        private const string LatestReleaseApi =
            "https://api.github.com/repos/OmarEAbdelaal/APG-Revit-Plugins/releases/latest";
        public const string ReleasesPage =
            "https://github.com/OmarEAbdelaal/APG-Revit-Plugins/releases";

        public static Version CurrentVersion
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return Normalize(v);
            }
        }

        public static async Task<UpdateInfo?> CheckAsync()
        {
            try
            {
#if REVIT2024
                // .NET Framework 4.8 does not always negotiate TLS 1.2 by default.
                System.Net.ServicePointManager.SecurityProtocol |=
                    System.Net.SecurityProtocolType.Tls12;
#endif
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("APG-Revit-Plugins-UpdateCheck");
                    string json = await client.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);

                    Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(?:\\.[0-9]+){1,3})\"");
                    if (!tag.Success)
                        return null;
                    Version latest = Normalize(Version.Parse(tag.Groups[1].Value));

                    Match page = Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]*/releases/tag/[^\"]+)\"");
                    Match asset = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.exe)\"");

                    return new UpdateInfo(
                        CurrentVersion,
                        latest,
                        page.Success ? page.Groups[1].Value : ReleasesPage,
                        asset.Success ? asset.Groups[1].Value : null);
                }
            }
            catch
            {
                return null;
            }
        }

        // ── Notify-once-per-version bookkeeping for the startup check ──────────

        private static string MarkerPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "APGRevitPlugins", "last-update-notice.txt");

        public static bool WasNotified(Version version)
        {
            try
            {
                return File.Exists(MarkerPath) &&
                       File.ReadAllText(MarkerPath).Trim() == version.ToString();
            }
            catch
            {
                return false;
            }
        }

        public static void MarkNotified(Version version)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                File.WriteAllText(MarkerPath, version.ToString());
            }
            catch
            {
                // best effort only
            }
        }

        /// <summary>3-component version so 1.1.0.0 and 1.1.0 compare as equal.</summary>
        private static Version Normalize(Version v)
            => new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
    }
}
