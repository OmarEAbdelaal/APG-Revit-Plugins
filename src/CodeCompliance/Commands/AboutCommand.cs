using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Shows basic information about the add-in: version, target Revit version,
    /// and a short description of what it will do.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string assemblyVersion =
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            string revitVersion = commandData.Application.Application.VersionNumber;

            var dialog = new TaskDialog("About APG Plugins")
            {
                MainInstruction = "APG Plugins - Code Compliance",
                MainContent =
                    "Reviews fire-fighting designs in Revit models against applicable codes, " +
                    "produces comprehensive review comments on plans, and creates code-compliant " +
                    "parking ramps (Dubai Building Code Annex B).\n\n" +
                    "Add-in version: " + assemblyVersion + "\n" +
                    "Running in Revit: " + revitVersion,
                FooterText = "Author: Omar Elsayed",
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dialog.Show();

            return Result.Succeeded;
        }
    }
}
