using System;
using System.IO;

namespace CodeCompliance.Core.Mcp
{
    /// <summary>
    /// Every file and folder the Revit MCP module uses. Everything lives under
    /// %LOCALAPPDATA%\APGRevitPlugins\RevitMCP so that updates never touch the add-in
    /// folder and the same server/commands serve every installed Revit version.
    ///
    /// <code>
    /// RevitMCP\
    ///   server\build\index.js        MCP server (Node.js) launched by Claude
    ///   server\runtime\node.exe      Node.js bundled with the server release
    ///   runtime\node.exe             Node.js downloaded by the plugin (survives server updates)
    ///   Commands\&lt;Set&gt;\command.json   one folder per command set
    ///   Commands\&lt;Set&gt;\&lt;year&gt;\*.dll   command DLLs per Revit version
    ///   data\                        SQLite data written by the server
    ///   Logs\                        socket service logs
    ///   installed.json               versions of the installed server / commands
    ///   mcp-settings.json            port, auto-start, disabled commands
    /// </code>
    /// </summary>
    public static class McpPaths
    {
        public static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "APGRevitPlugins", "RevitMCP");

        public static string ServerDir => Path.Combine(Root, "server");
        public static string ServerEntry => Path.Combine(ServerDir, "build", "index.js");
        public static string ServerPackageJson => Path.Combine(ServerDir, "package.json");

        /// <summary>node.exe shipped inside the server release (preferred).</summary>
        public static string BundledNodeExe => Path.Combine(ServerDir, "runtime", "node.exe");

        /// <summary>node.exe downloaded by the plugin; outside server\ so server updates keep it.</summary>
        public static string DownloadedNodeDir => Path.Combine(Root, "runtime");
        public static string DownloadedNodeExe => Path.Combine(DownloadedNodeDir, "node.exe");

        public static string CommandsDir => Path.Combine(Root, "Commands");
        public static string DataDir => Path.Combine(Root, "data");
        public static string LogsDir => Path.Combine(Root, "Logs");
        public static string InstalledFile => Path.Combine(Root, "installed.json");
        public static string SettingsFile => Path.Combine(Root, "mcp-settings.json");
        public static string TempDir => Path.Combine(Root, "tmp");

        /// <summary>Claude Desktop's MCP configuration file.</summary>
        public static string ClaudeConfigFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude", "claude_desktop_config.json");

        /// <summary>Claude Code's user configuration (only written when it already exists).</summary>
        public static string ClaudeCodeConfigFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude.json");

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(CommandsDir);
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LogsDir);
        }
    }
}
