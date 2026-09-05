using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Opens the Revit MCP setup window: install / update the MCP server and command sets
    /// from GitHub, configure Claude Desktop, start or stop the server and choose which
    /// commands are exposed to the AI.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class McpSetupCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Modal: keeps the Revit API context alive so the Start button can load commands.
            new UI.McpSetupWindow(commandData.Application).ShowDialog();
            return Result.Succeeded;
        }
    }
}
