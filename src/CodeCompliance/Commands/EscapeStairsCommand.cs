using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using CodeCompliance.Core;
using CodeCompliance.UI;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Step 1 of the egress workflow: detects all stairs in the model, lets the user
    /// mark which ones are escape stairs, and saves the choice to the
    /// CC_IsEscapeStair parameter on each stair.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class EscapeStairsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Escape Stairs", "Please open a Revit model first.");
                return Result.Cancelled;
            }
            Document doc = uiDoc.Document;

            IList<Stairs> stairs = EscapeStairService.CollectStairs(doc);
            if (stairs.Count == 0)
            {
                TaskDialog.Show("Escape Stairs", "No stairs were found in this model.");
                return Result.Cancelled;
            }

            // Make sure the parameter exists before reading current values.
            using (var t = new Transaction(doc, "Ensure escape stair parameter"))
            {
                t.Start();
                EscapeStairService.EnsureParameter(doc);
                t.Commit();
            }

            List<EscapeStairItem> items = stairs.Select(s => new EscapeStairItem
            {
                Id = s.Id.Value,
                Name = s.Name,
                TypeName = doc.GetElement(s.GetTypeId())?.Name ?? "",
                BaseLevel = (doc.GetElement(s.LevelId) as Level)?.Name ?? "",
                IsEscape = EscapeStairService.IsEscapeStair(s)
            }).ToList();

            var window = new EscapeStairsWindow(items);
            window.ShowDialog();
            if (!window.Confirmed)
                return Result.Cancelled;

            int escapeCount = 0;
            using (var t = new Transaction(doc, "Set escape stairs"))
            {
                t.Start();
                foreach (EscapeStairItem item in items)
                {
                    Element? stair = doc.GetElement(new ElementId(item.Id));
                    if (stair == null)
                        continue;
                    EscapeStairService.SetEscapeStair(stair, item.IsEscape);
                    if (item.IsEscape)
                        escapeCount++;
                }
                t.Commit();
            }

            TaskDialog.Show("Escape Stairs",
                $"Saved: {escapeCount} of {stairs.Count} stairs are marked as escape stairs.\n\n" +
                "Next step: open a floor plan and run 'Travel Paths'.");
            return Result.Succeeded;
        }
    }
}
