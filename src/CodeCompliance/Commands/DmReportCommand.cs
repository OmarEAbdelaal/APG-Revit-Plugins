using System;
using System.Diagnostics;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CodeCompliance.Core.Dm;
using CodeCompliance.Reporting;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Runs the DM BIM compliance audit without opening the dashboard and writes the report
    /// files (HTML dashboard, CSV of all findings with element ids, and the Revit MCP prompts)
    /// to Documents\CodeCompliance.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DmReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("DM Compliance Report", "Please open a Revit model first.");
                return Result.Cancelled;
            }

            DmAuditResult result;
            try
            {
                result = DmAuditService.Run(uiDoc.Document, commandData.Application.Application.VersionNumber,
                                            new DmAuditOptions());
            }
            catch (Exception ex)
            {
                TaskDialog.Show("DM Compliance Report", "The audit failed:\n" + ex.Message);
                return Result.Failed;
            }

            (string htmlPath, string csvPath, string promptPath) = DmComplianceReportWriter.Write(result);

            var summary = new StringBuilder();
            summary.AppendLine("Critical: " + result.Count(DmSeverity.Critical));
            summary.AppendLine("Errors:   " + result.Count(DmSeverity.Error));
            summary.AppendLine("Warnings: " + result.Count(DmSeverity.Warning));
            summary.AppendLine("Elements to modify: " + result.AffectedElements);
            summary.AppendLine("Submission readiness: " + result.ReadinessPercent + "%");
            summary.AppendLine();
            summary.AppendLine("Files:");
            summary.AppendLine("  " + htmlPath);
            summary.AppendLine("  " + csvPath);
            summary.AppendLine("  " + promptPath);

            var dialog = new TaskDialog("DM Compliance Report")
            {
                MainInstruction = "DM BIM compliance audit complete",
                MainContent = summary.ToString(),
                FooterText = "Use DM Compliance for the interactive dashboard, the 3D section box highlight " +
                             "and the Claude fix prompts.",
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dialog.Show();

            try
            {
                Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute = true });
            }
            catch
            {
                // opening the browser is a convenience only
            }

            return Result.Succeeded;
        }
    }
}
