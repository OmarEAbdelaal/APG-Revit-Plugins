using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Placeholder "smoke test" command. It proves the add-in loads and can read the model:
    /// it counts the main fire-protection element categories in the active document and
    /// shows a summary dialog. The real rule engine will replace the body of this command.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class FireFightingCheckCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Fire Fighting Check", "Please open a Revit model first.");
                return Result.Cancelled;
            }

            Document doc = uiDoc.Document;

            int sprinklers = CountElements(doc, BuiltInCategory.OST_Sprinklers);
            int pipes = CountElements(doc, BuiltInCategory.OST_PipeCurves);
            int fittings = CountElements(doc, BuiltInCategory.OST_PipeFitting);
            int accessories = CountElements(doc, BuiltInCategory.OST_PipeAccessory);
            int equipment = CountElements(doc, BuiltInCategory.OST_MechanicalEquipment);

            var summary = new StringBuilder();
            summary.AppendLine("Fire-protection element summary for: " + doc.Title);
            summary.AppendLine();
            summary.AppendLine("Sprinklers:            " + sprinklers);
            summary.AppendLine("Pipes:                 " + pipes);
            summary.AppendLine("Pipe fittings:         " + fittings);
            summary.AppendLine("Pipe accessories:      " + accessories);
            summary.AppendLine("Mechanical equipment:  " + equipment);

            var dialog = new TaskDialog("Fire Fighting Check")
            {
                MainInstruction = "Plugin is installed and working.",
                MainContent = summary.ToString(),
                FooterText = "Detailed code-compliance checks are coming in the next version.",
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dialog.Show();

            return Result.Succeeded;
        }

        private static int CountElements(Document doc, BuiltInCategory category)
        {
            using (var collector = new FilteredElementCollector(doc))
            {
                return collector
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .GetElementCount();
            }
        }
    }
}
