using System;
using System.IO;

namespace CodeCompliance.Core.Mcp
{
    /// <summary>
    /// Minimal daily log file for the MCP module (Logs\mcp_yyyyMMdd.log). Never throws:
    /// logging must not be able to break Revit or a running command.
    /// </summary>
    public static class McpLog
    {
        private static readonly object Gate = new object();

        public static string CurrentFile => Path.Combine(McpPaths.LogsDir, "mcp_" + DateTime.Now.ToString("yyyyMMdd") + ".log");

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);
        public static void Error(string message) => Write("ERROR", message);
        public static void Error(string message, Exception ex) => Write("ERROR", message + ": " + ex.GetType().Name + " - " + ex.Message);

        private static void Write(string level, string message)
        {
            try
            {
                Directory.CreateDirectory(McpPaths.LogsDir);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message + Environment.NewLine;
                lock (Gate)
                {
                    File.AppendAllText(CurrentFile, line);
                }
                System.Diagnostics.Debug.WriteLine("[RevitMCP] " + message);
            }
            catch
            {
                // best effort only
            }
        }
    }
}
