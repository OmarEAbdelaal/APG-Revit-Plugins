using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CodeCompliance.Core;
using CodeCompliance.Reporting;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Step 3 of the egress workflow: measures every travel path created by this plugin,
    /// detects the doors each path passes through and their fire ratings, creates the
    /// Revit schedules, and exports an HTML + CSV report.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class EgressReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Egress Report", "Please open a Revit model first.");
                return Result.Cancelled;
            }
            Document doc = uiDoc.Document;

            IList<Element> paths = EgressAnalysisService.CollectPluginPaths(doc);
            if (paths.Count == 0)
            {
                TaskDialog.Show("Egress Report",
                    "No travel paths were found.\n" +
                    "Run 'Escape Stairs' and then 'Travel Paths' on a floor plan first.");
                return Result.Cancelled;
            }

            IList<FamilyInstance> allDoors = EgressAnalysisService.CollectDoors(doc);

            var results = new List<EgressPathResult>();
            foreach (Element path in paths)
            {
                string mark = path.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                View? view = doc.GetElement(path.OwnerViewId) as View;
                var result = new EgressPathResult
                {
                    PathId = path.Id,
                    RoomName = mark.StartsWith(EgressAnalysisService.PathMarkPrefix)
                        ? mark.Substring(EgressAnalysisService.PathMarkPrefix.Length)
                        : mark,
                    LevelName = (view as ViewPlan)?.GenLevel?.Name ?? view?.Name ?? "",
                    LengthMeters = EgressAnalysisService.GetPathLengthMeters(path)
                };
                foreach (FamilyInstance door in EgressAnalysisService.FindDoorsOnPath(path, allDoors))
                    result.Doors.Add(EgressAnalysisService.DescribeDoor(doc, door));
                results.Add(result);
            }

            using (var t = new Transaction(doc, "Create egress schedules"))
            {
                t.Start();
                ScheduleBuilder.EnsureSchedules(doc);
                t.Commit();
            }

            (string csvPath, string htmlPath) = EgressReportWriter.Write(doc.Title, results);

            EgressPathResult longest = results.OrderByDescending(r => r.LengthMeters).First();
            int doorsOnRoutes = results.SelectMany(r => r.Doors).GroupBy(d => d.DoorId).Count();
            int unrated = results.SelectMany(r => r.Doors).GroupBy(d => d.DoorId)
                .Select(g => g.First()).Count(d => d.FireRating == "Not rated");

            var summary = new StringBuilder();
            summary.AppendLine($"Travel paths analysed: {results.Count}");
            summary.AppendLine("Longest travel distance: " +
                longest.LengthMeters.ToString("F2", CultureInfo.InvariantCulture) +
                $" m ({longest.RoomName})");
            summary.AppendLine($"Doors on escape routes: {doorsOnRoutes}");
            if (unrated > 0)
                summary.AppendLine($"WARNING: {unrated} door(s) on escape routes have no fire rating.");
            summary.AppendLine();
            summary.AppendLine("Schedules created in the project browser:");
            summary.AppendLine("  - " + ScheduleBuilder.PathScheduleName);
            summary.AppendLine("  - " + ScheduleBuilder.DoorScheduleName);
            summary.AppendLine();
            summary.AppendLine("Report files:");
            summary.AppendLine("  " + htmlPath);
            summary.AppendLine("  " + csvPath);

            var dialog = new TaskDialog("Egress Report")
            {
                MainInstruction = "Egress analysis complete",
                MainContent = summary.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close
            };
            dialog.Show();
            return Result.Succeeded;
        }
    }
}
