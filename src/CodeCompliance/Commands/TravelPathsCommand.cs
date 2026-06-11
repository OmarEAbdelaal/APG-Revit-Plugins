using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using CodeCompliance.Core;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Step 2 of the egress workflow: for every room on the active floor plan's level,
    /// creates a Revit "Path of Travel" from the most remote point of the room to the
    /// nearest escape stair. Revit routes the path around walls and through doors.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class TravelPathsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Travel Paths", "Please open a Revit model first.");
                return Result.Cancelled;
            }
            Document doc = uiDoc.Document;

            if (doc.ActiveView is not ViewPlan viewPlan || viewPlan.GenLevel == null)
            {
                TaskDialog.Show("Travel Paths",
                    "Paths of travel can only be created in a floor plan view.\n" +
                    "Please open the floor plan of the level you want to analyse and run the command again.");
                return Result.Cancelled;
            }
            Level level = viewPlan.GenLevel;

            List<Stairs> escapeStairs = EscapeStairService.CollectStairs(doc)
                .Where(EscapeStairService.IsEscapeStair)
                .ToList();
            if (escapeStairs.Count == 0)
            {
                TaskDialog.Show("Travel Paths",
                    "No escape stairs are marked in this model.\n" +
                    "Run 'Escape Stairs' first and tick the stairs used for escape.");
                return Result.Cancelled;
            }

            List<Room> rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => r.Area > 0 && r.LevelId == level.Id)
                .ToList();
            if (rooms.Count == 0)
            {
                TaskDialog.Show("Travel Paths",
                    $"No placed rooms were found on level '{level.Name}'.\n" +
                    "Place rooms first - paths of travel are calculated from rooms to escape stairs.");
                return Result.Cancelled;
            }

            double z = level.Elevation;
            var stairTargets = escapeStairs.ToDictionary(
                s => s,
                s => EgressAnalysisService.GetStairTargetPoints(doc, s, z));

            int created = 0;
            int deleted;
            var failures = new List<string>();

            using (var t = new Transaction(doc, "Create egress travel paths"))
            {
                t.Start();
                EgressAnalysisService.EnsureDoorsArePassable(doc);
                deleted = EgressAnalysisService.DeleteExistingPaths(doc, viewPlan);

                foreach (Room room in rooms)
                {
                    XYZ roomCenter = (room.Location as LocationPoint)?.Point ?? XYZ.Zero;

                    // Nearest escape stair (straight-line) is the egress target for this room.
                    Stairs nearest = escapeStairs
                        .OrderBy(s => stairTargets[s].Count == 0
                            ? double.MaxValue
                            : EgressAnalysisService.Distance2D(stairTargets[s][0], roomCenter))
                        .First();

                    PathOfTravel? path = null;
                    foreach (XYZ target in stairTargets[nearest])
                    {
                        path = EgressAnalysisService.CreatePathFromRoom(viewPlan, room, target);
                        if (path != null)
                            break;
                    }

                    if (path == null)
                        failures.Add(room.Name);
                    else
                        created++;
                }
                t.Commit();
            }

            var summary = new StringBuilder();
            summary.AppendLine($"Level: {level.Name}");
            summary.AppendLine($"Escape stairs considered: {escapeStairs.Count}");
            summary.AppendLine($"Paths created: {created} of {rooms.Count} rooms");
            if (deleted > 0)
                summary.AppendLine($"(replaced {deleted} previously created paths)");
            if (failures.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("No route found for:");
                foreach (string name in failures.Take(10))
                    summary.AppendLine("  - " + name);
                if (failures.Count > 10)
                    summary.AppendLine($"  ... and {failures.Count - 10} more");
            }

            TaskDialog.Show("Travel Paths", summary.ToString() +
                "\nNext step: run 'Egress Report' to measure paths, check door fire ratings " +
                "and generate the report and schedules.");
            return Result.Succeeded;
        }
    }
}
