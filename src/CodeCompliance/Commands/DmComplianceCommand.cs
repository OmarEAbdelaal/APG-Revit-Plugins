using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Opens the DM BIM Compliance dashboard: audits the open model against the Dubai
    /// Municipality BIM e-submission requirements, lists every element that has to be modified
    /// with the type of modification, frames those elements in a 3D section box and hands out
    /// the Revit MCP prompt that lets Claude apply the fix.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DmComplianceCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("DM BIM Compliance", "Please open a Revit model first.");
                return Result.Cancelled;
            }

            // Modal, so the Revit API context stays alive for the audit and the section box view.
            var window = new UI.DmComplianceWindow(commandData.Application);
            window.ShowDialog();

            // The dashboard only requests the view change; activating and zooming has to wait
            // until the modal dialog is gone.
            if (window.HighlightViewId != null && window.HighlightViewId != ElementId.InvalidElementId)
            {
                try
                {
                    if (uiDoc.Document.GetElement(window.HighlightViewId) is View view)
                    {
                        uiDoc.ActiveView = view;
                        if (window.HighlightElements.Count > 0)
                            uiDoc.ShowElements(window.HighlightElements);
                    }
                }
                catch
                {
                    // the user may have closed or deleted the view meanwhile
                }
            }

            return Result.Succeeded;
        }
    }
}
