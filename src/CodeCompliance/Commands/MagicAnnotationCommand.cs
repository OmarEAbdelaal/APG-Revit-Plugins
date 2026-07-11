using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CodeCompliance.Core.Annotation;
using CodeCompliance.UI;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Magic Annotation: annotates the active plan, section or elevation view in one
    /// step. The user ticks the wanted annotation types in a checklist (dimensions,
    /// tags, spot elevations, ramp slopes, stair arrows); everything is placed inside
    /// a single transaction, re-runs replace the previous result, and a summary lists
    /// what was created plus suggested callouts.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class MagicAnnotationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Magic Annotation", "Please open a Revit model first.");
                return Result.Cancelled;
            }
            Document doc = uiDoc.Document;
            View view = uiDoc.ActiveView;

            bool isPlan = view.ViewType == ViewType.FloorPlan
                || view.ViewType == ViewType.CeilingPlan
                || view.ViewType == ViewType.EngineeringPlan
                || view.ViewType == ViewType.AreaPlan;
            bool isSectionOrElevation = view.ViewType == ViewType.Section
                || view.ViewType == ViewType.Elevation;
            if (!isPlan && !isSectionOrElevation)
            {
                TaskDialog.Show("Magic Annotation",
                    "Magic Annotation works in plan, section and elevation views.\n\n" +
                    "Open one of those views and run the command again.");
                return Result.Cancelled;
            }
            if (view.IsTemplate)
            {
                TaskDialog.Show("Magic Annotation", "The active view is a view template. Open a real view first.");
                return Result.Cancelled;
            }

            var window = new MagicAnnotationWindow(view.Name, view.Scale, isPlan);
            window.ShowDialog();
            if (!window.Confirmed)
                return Result.Cancelled;
            if (!window.AnythingTicked())
            {
                TaskDialog.Show("Magic Annotation", "Nothing was ticked, so nothing was placed.");
                return Result.Cancelled;
            }

            AnnotationOptions options = window.BuildOptions();
            AnnotationResult result;
            using (var t = new Transaction(doc, "Magic Annotation"))
            {
                t.Start();
                try
                {
                    result = MagicAnnotationService.Run(doc, view, options);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    message = "Magic Annotation failed: " + ex.Message;
                    return Result.Failed;
                }
                t.Commit();
            }

            ShowSummary(result);
            return Result.Succeeded;
        }

        private static void ShowSummary(AnnotationResult result)
        {
            var text = new StringBuilder();

            if (result.Total == 0)
                text.AppendLine("No annotations were created.");
            else
                foreach (KeyValuePair<string, int> pair in result.Counts)
                    text.AppendLine(pair.Key + ": " + pair.Value);

            if (result.Removed > 0)
                text.AppendLine().AppendLine("Replaced " + result.Removed + " annotation(s) from the previous run.");

            if (result.CalloutSuggestions.Count > 0)
            {
                text.AppendLine().AppendLine("Suggested callouts (not created automatically):");
                int shown = 0;
                foreach (string suggestion in result.CalloutSuggestions)
                {
                    text.AppendLine("  • " + suggestion);
                    if (++shown == 12)
                    {
                        int rest = result.CalloutSuggestions.Count - shown;
                        if (rest > 0)
                            text.AppendLine("  … and " + rest + " more.");
                        break;
                    }
                }
            }

            var dialog = new TaskDialog("Magic Annotation")
            {
                MainInstruction = result.Total > 0
                    ? "Placed " + result.Total + " annotation(s)."
                    : "Nothing to annotate in this view.",
                MainContent = text.ToString().TrimEnd()
            };

            if (result.Warnings.Count > 0)
                dialog.ExpandedContent = "Notes:\n• " + string.Join("\n• ", result.Warnings);

            dialog.CommonButtons = TaskDialogCommonButtons.Close;
            dialog.Show();
        }
    }
}
