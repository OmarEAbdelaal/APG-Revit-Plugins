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

            var dialog = new TaskDialog("About Code Compliance")
            {
                MainInstruction = "Code Compliance - Fire Fighting",
                MainContent =
                    "Reviews fire-fighting designs in Revit models against applicable codes " +
                    "and produces comprehensive review comments on plans.\n\n" +
                    "Add-in version: " + assemblyVersion + "\n" +
                    "Running in Revit: " + revitVersion,
                FooterText = "Author: Omar E. Abdelaal",
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dialog.Show();

            return Result.Succeeded;
        }
    }
}
