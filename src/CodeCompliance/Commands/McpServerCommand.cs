using System;
using System.Net.Sockets;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CodeCompliance.Core.Mcp;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Ribbon toggle for the Revit MCP socket service: starts it (loading the command sets
    /// for this Revit version) or stops it. While it runs, the MCP server launched by Claude
    /// can read and drive this Revit session.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class McpServerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            McpSocketService service = McpSocketService.Instance;
            try
            {
                if (service.IsRunning)
                {
                    service.Stop();
                    TaskDialog.Show("Revit MCP", "MCP server switched OFF.\n\nClaude can no longer reach this Revit session.");
                    return Result.Succeeded;
                }

                McpSettings settings = McpSettings.Load();
                if (!McpInstaller.IsCommandsInstalled)
                {
                    var missing = new TaskDialog("Revit MCP")
                    {
                        MainInstruction = "The Revit MCP command sets are not installed yet.",
                        MainContent = "Open MCP Setup on the APG Revit Plugins tab and click Install / Update to download " +
                                      "the MCP server and the Revit command sets from GitHub. The server can still be " +
                                      "started now, but only the built-in ping / search_modules commands will be available.",
                        CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                        DefaultButton = TaskDialogResult.Cancel
                    };
                    if (missing.Show() != TaskDialogResult.Ok)
                        return Result.Cancelled;
                }

                service.Start(commandData.Application, settings);

                var dialog = new TaskDialog("Revit MCP")
                {
                    MainInstruction = "MCP server switched ON (port " + service.Port + ").",
                    MainContent = service.CommandCount + " commands are available to Claude for Revit " +
                                  commandData.Application.Application.VersionNumber + ".\n\n" +
                                  "In Claude Desktop the Revit tools appear once Claude has been configured " +
                                  "(MCP Setup > Configure Claude Desktop) and restarted.",
                    FooterText = "Log: " + McpLog.CurrentFile,
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                dialog.Show();
                return Result.Succeeded;
            }
            catch (SocketException ex)
            {
                TaskDialog.Show("Revit MCP",
                    "Could not open port " + McpSettings.Load().Port + ": " + ex.Message +
                    "\n\nAnother Revit session (or another program) is probably already listening on it. " +
                    "Switch the MCP server off in the other session, or change the port in MCP Setup.");
                return Result.Failed;
            }
            catch (Exception ex)
            {
                McpLog.Error("MCP server start failed", ex);
                message = ex.Message;
                TaskDialog.Show("Revit MCP", "Could not start the MCP server:\n" + ex.Message);
                return Result.Failed;
            }
        }
    }
}
