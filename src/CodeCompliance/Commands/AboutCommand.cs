using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Shows the APG Revit Plugins about dialog: suite version, plugins included,
    /// company and author information.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string revitVersion = commandData.Application.Application.VersionNumber;
            new UI.AboutWindow(revitVersion).ShowDialog();
            return Result.Succeeded;
        }
    }
}
