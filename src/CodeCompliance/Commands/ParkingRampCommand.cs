using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CodeCompliance.Core;
using CodeCompliance.UI;

namespace CodeCompliance.Commands
{
    /// <summary>
    /// Creates a code-compliant parking ramp (Dubai Building Code Annex B,
    /// Section B.7.2.2, Tables B.9 / B.10) as native Floor elements shaped with
    /// slab-shape ("Modify Sub Elements") points:
    /// 1. The user picks how to define the ramp path: select an already-drawn chain of
    ///    model lines/arcs (as the left edge, right edge or centerline), draw new
    ///    lines/arcs now with Revit's own Line tool (then re-run to select them), or
    ///    select the ramp's full drawn outline (left edge, right edge, start, end) for a
    ///    ramp whose width varies along its run. The ramp always starts at the start
    ///    point of the first line/curve drawn or picked, in the direction of travel.
    /// 2. The dialog asks for two of the three key parameters (floor height h,
    ///    slope S, total run R) and solves for the third, exactly like the
    ///    standalone Parking Ramp Calculator app, with code compliance checked
    ///    live at the input step. Non-compliant designs cannot be created.
    /// 3. The ramp (entry transition + main run + exit transition) is created as
    ///    one or more floors with their sub-element points set to the profile.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ParkingRampCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Parking Ramp", "Please open a Revit model first.");
                return Result.Cancelled;
            }
            Document doc = uiDoc.Document;

            // ── Step 1: get the ramp path — select drawn lines or draw it now ──
            RampPath path;
            try
            {
                RampPath? picked = AcquirePath(uiDoc, doc);
                if (picked == null)
                    return Result.Cancelled;
                path = picked;
            }
            catch (RampCalcException ex)
            {
                TaskDialog.Show("Parking Ramp", ex.Message);
                return Result.Cancelled;
            }

            // ── Step 2: parameters + live code compliance ────────────────────────
            List<FloorTypeItem> floorTypes = CollectFloorTypes(doc);
            if (floorTypes.Count == 0)
            {
                TaskDialog.Show("Parking Ramp", "The model has no floor types to build the ramp with.");
                return Result.Cancelled;
            }
            long defaultTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType)?.Value ?? floorTypes[0].Id;

            var window = new RampInputWindow(path, floorTypes, defaultTypeId);
            window.ShowDialog();
            if (!window.Confirmed || window.Result == null)
                return Result.Cancelled;

            RampCalcResult calc = window.Result;

            // ── Step 3: create the floors and shape their sub-element points ────
            IList<ElementId> floorIds;
            using (var t = new Transaction(doc, "Create parking ramp floors"))
            {
                t.Start();
                try
                {
                    floorIds = RampFloorBuilder.Build(
                        doc, path, calc, window.TotalWidth, window.Location,
                        new ElementId(window.FloorTypeId));
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    message = "Failed to build the ramp floors: " + ex.Message;
                    return Result.Failed;
                }

                string summary = BuildSummary(calc, window);
                int index = 1;
                foreach (ElementId id in floorIds)
                {
                    Element? floor = doc.GetElement(id);
                    if (floor == null)
                        continue;
                    Parameter mark = floor.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (mark != null && !mark.IsReadOnly)
                        mark.Set($"CC - Ramp {index}/{floorIds.Count}");
                    Parameter comments = floor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (comments != null && !comments.IsReadOnly)
                        comments.Set(summary);
                    index++;
                }
                t.Commit();
            }

            TaskDialog.Show("Parking Ramp",
                $"Ramp created as {floorIds.Count} floor(s) with slab-shape points.\n\n" +
                BuildSummary(calc, window) +
                "\n\nAll Table B.9 checks passed at the input step. " +
                "The full design data is stored in each floor's Comments parameter.");
            return Result.Succeeded;
        }

        /// <summary>
        /// Lets the user choose how to define the path: select existing model lines/arcs
        /// (a single path, or the ramp's full left/right/start/end outline for a ramp whose
        /// width varies along its run), or draw new geometry now with Revit's own Line tool.
        /// Returns null on cancel.
        /// </summary>
        private static RampPath? AcquirePath(UIDocument uiDoc, Document doc)
        {
            var choice = new TaskDialog("Parking Ramp")
            {
                MainInstruction = "How do you want to define the ramp path?",
                MainContent =
                    "Whichever way you provide it, the ramp starts at the START POINT of the " +
                    "first line/curve you draw or pick — draw or pick it in the direction of " +
                    "travel, going up the ramp.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                AllowCancellation = true
            };
            choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Select drawn model lines",
                "Pick one or more connected model lines/arcs forming a single path (left edge, " +
                "right edge or centerline — chosen next). Pick, or start the chain at, the line " +
                "at the ramp's bottom first: the point where it begins is the ramp's starting " +
                "point, and its drawn direction sets the direction of travel going up.");
            choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Draw new lines/arcs now",
                "Opens Revit's own Line tool (Line and Arc, with Chain) so you can sketch the " +
                "path with full snapping and dimension input. Once you're done, run Parking Ramp " +
                "again and choose \"Select drawn model lines\" to continue with what you drew.");
            choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
                "Select ramp outline (varying width)",
                "Pick the ramp's left edge, right edge, start edge and end edge separately — " +
                "each one or more connected lines/arcs. Use this when the ramp is not a constant " +
                "width along its length.");
            TaskDialogResult result = choice.Show();

            if (result == TaskDialogResult.CommandLink1)
            {
                IList<Curve>? curves = PickCurveChain(uiDoc, doc,
                    "Select the model lines/arcs forming the ramp path (first pick = ramp start), " +
                    "then press Finish.");
                return curves == null ? null : RampPath.FromCurves(curves);
            }

            if (result == TaskDialogResult.CommandLink2)
            {
                var instructions = new TaskDialog("Parking Ramp - Draw Path")
                {
                    MainInstruction = "Revit's Line tool will open next.",
                    MainContent =
                        "Use Line and Arc (start-end-radius, center-ends, tangent or fillet), with " +
                        "\"Chain\" turned on, to sketch the ramp path as one continuous run of lines " +
                        "and curves, in the direction of travel going up the ramp — the point where " +
                        "you start sketching becomes the ramp's starting point.\n\n" +
                        "When you're done, click Modify (or press Esc), then run Parking Ramp again " +
                        "and choose \"Select drawn model lines\" (or \"Select ramp outline\" for a " +
                        "varying-width ramp) to continue with what you just drew.",
                    CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.Ok
                };
                if (instructions.Show() != TaskDialogResult.Ok)
                    return null;

                RevitCommandId lineCmd = RevitCommandId.LookupPostableCommandId(PostableCommand.ModelLine);
                if (lineCmd != null && uiDoc.Application.CanPostCommand(lineCmd))
                    uiDoc.Application.PostCommand(lineCmd);
                else
                    TaskDialog.Show("Parking Ramp",
                        "Could not open the Line tool automatically. Click Model Line on the " +
                        "ribbon, draw the path, then run Parking Ramp again.");
                return null;
            }

            if (result == TaskDialogResult.CommandLink3)
                return AcquireOutlinePath(uiDoc, doc);

            return null;
        }

        /// <summary>
        /// Prompts for the ramp's full drawn outline — left edge, right edge, start edge and
        /// end edge, each its own chain of one or more connected lines/arcs — and builds a
        /// path whose width can vary along the run (see <see cref="RampPath.FromOutline"/>).
        /// </summary>
        private static RampPath? AcquireOutlinePath(UIDocument uiDoc, Document doc)
        {
            TaskDialog.Show("Parking Ramp - Ramp Outline",
                "Select the ramp's drawn outline in four picks — left edge, right edge, start " +
                "edge and end edge — each made of one or more connected model lines/arcs. The " +
                "left/right edges may differ in length or shape, so the ramp width can vary " +
                "along the run; the start/end lines just confirm the two ends connect up. Pick " +
                "the edges in the direction of travel, going up the ramp — the left edge's " +
                "starting point sets the ramp's starting point.");

            IList<Curve>? leftCurves = PickCurveChain(uiDoc, doc,
                "Select the LEFT edge of the ramp (one or more connected lines/arcs), then press Finish.");
            if (leftCurves == null)
                return null;

            IList<Curve>? rightCurves = PickCurveChain(uiDoc, doc,
                "Select the RIGHT edge of the ramp (one or more connected lines/arcs), then press Finish.");
            if (rightCurves == null)
                return null;

            IList<Curve>? startCurves = PickCurveChain(uiDoc, doc,
                "Select the line at the START of the ramp, connecting the left and right edges, " +
                "then press Finish.");
            if (startCurves == null)
                return null;

            IList<Curve>? endCurves = PickCurveChain(uiDoc, doc,
                "Select the line at the END of the ramp, connecting the left and right edges, " +
                "then press Finish.");
            if (endCurves == null)
                return null;

            return RampPath.FromOutline(leftCurves, rightCurves, startCurves, endCurves);
        }

        /// <summary>Picks one or more model lines/arcs and returns their geometry curves, or
        /// null when the user cancels or picks nothing.</summary>
        private static IList<Curve>? PickCurveChain(UIDocument uiDoc, Document doc, string prompt)
        {
            IList<Reference> refs;
            try
            {
                refs = uiDoc.Selection.PickObjects(ObjectType.Element, new RampLineSelectionFilter(), prompt);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }

            var curves = new List<Curve>();
            foreach (Reference r in refs)
                if (doc.GetElement(r) is CurveElement ce && ce.GeometryCurve != null)
                    curves.Add(ce.GeometryCurve);
            return curves.Count > 0 ? curves : null;
        }

        private static List<FloorTypeItem> CollectFloorTypes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .Where(ft => !ft.IsFoundationSlab)
                .Select(ft => new FloorTypeItem
                {
                    Name = ft.Name,
                    Id = ft.Id.Value,
                    ThicknessM = UnitUtils.ConvertFromInternalUnits(
                        ft.GetCompoundStructure()?.GetWidth() ?? 0, UnitTypeId.Meters)
                })
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildSummary(RampCalcResult calc, RampInputWindow window)
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Format(ci,
                "Dubai BC Annex B B.7.2.2 | {0} ramp, {1} lane(s) x {2:F2} m | " +
                "h = {3:F3} m, S = {4:F2}%, T = {5:F2}%, X = {6:F2} m, X' = {7:F2} m, R = {8:F3} m | " +
                "path = {9}",
                calc.Type, window.Lanes, window.LaneWidth,
                calc.H, calc.S, calc.T, calc.X, calc.XPrime, calc.R,
                window.Location);
        }

        /// <summary>Allows picking only straight or arc model/detail lines.</summary>
        private class RampLineSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (!(elem is CurveElement ce))
                    return false;
                Curve? curve = ce.GeometryCurve;
                return curve is Line || curve is Arc;
            }

            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}
