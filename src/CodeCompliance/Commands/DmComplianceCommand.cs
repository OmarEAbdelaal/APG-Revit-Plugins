using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CodeCompliance.Core.Dm;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Opens the DM BIM Compliance dashboard: audits the open model against the Dubai
    /// Municipality BIM e-submission requirements and its recommended modelling practices,
    /// lists every element that has to be modified with the type of modification, frames those
    /// elements in a 3D section box and hands out the Revit MCP prompt that lets Claude apply
    /// the fix.
    ///
    /// The dashboard is <b>modeless</b>: Revit keeps working while it is open, so an element
    /// can be highlighted here and then edited in Revit without closing anything. The Revit
    /// API context the dashboard needs comes from <see cref="DmRevitTask"/>, created here (in a
    /// valid API context) and disposed when the window closes. Only one dashboard is open at a
    /// time; clicking the button again brings the existing one forward.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DmComplianceCommand : IExternalCommand
    {
        private static UI.DmComplianceWindow? _window;
        private static DmRevitTask? _task;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("DM BIM Compliance", "Please open a Revit model first.");
                return Result.Cancelled;
            }

            if (_window != null)
            {
                try
                {
                    if (_window.WindowState == System.Windows.WindowState.Minimized)
                        _window.WindowState = System.Windows.WindowState.Normal;
                    _window.Activate();
                    return Result.Succeeded;
                }
                catch
                {
                    // the window was closed without us noticing: fall through and open a new one
                    _window = null;
                }
            }

            // Created here because ExternalEvent.Create needs a Revit API context; it is what
            // lets the modeless window call back into Revit.
            _task = DmRevitTask.Create();
            _window = new UI.DmComplianceWindow(commandData.Application, _task);
            _window.Closed += (_, _) =>
            {
                _task?.Dispose();
                _task = null;
                _window = null;
            };
            _window.Show();

            return Result.Succeeded;
        }
    }
}
