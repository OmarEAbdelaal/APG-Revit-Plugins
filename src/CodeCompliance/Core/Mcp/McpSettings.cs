using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace CodeCompliance.Core.Mcp
{
    /// <summary>User settings of the Revit MCP module (mcp-settings.json).</summary>
    public sealed class McpSettings
    {
        public const int DefaultPort = 8080;

        /// <summary>TCP port the socket service listens on (the MCP server connects here).</summary>
        [JsonProperty("port")]
        public int Port { get; set; } = DefaultPort;

        /// <summary>Start the socket service automatically when Revit starts.</summary>
        [JsonProperty("autoStart")]
        public bool AutoStart { get; set; }

        /// <summary>Accept connections from other machines (default: this computer only).</summary>
        [JsonProperty("allowRemoteConnections")]
        public bool AllowRemoteConnections { get; set; }

        /// <summary>Commands the user switched off in the setup window.</summary>
        [JsonProperty("disabledCommands")]
        public List<string> DisabledCommands { get; set; } = new List<string>();

        /// <summary>Check GitHub for newer server/commands on Revit startup and install silently.</summary>
        [JsonProperty("autoUpdate")]
        public bool AutoUpdate { get; set; } = true;

        public static McpSettings Load()
        {
            try
            {
                if (File.Exists(McpPaths.SettingsFile))
                {
                    var loaded = JsonConvert.DeserializeObject<McpSettings>(File.ReadAllText(McpPaths.SettingsFile));
                    if (loaded != null)
                    {
                        if (loaded.Port <= 0 || loaded.Port > 65535) loaded.Port = DefaultPort;
                        loaded.DisabledCommands ??= new List<string>();
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Error("Could not read mcp-settings.json, using defaults", ex);
            }
            return new McpSettings();
        }

        public void Save()
        {
            McpPaths.EnsureDirectories();
            File.WriteAllText(McpPaths.SettingsFile, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
