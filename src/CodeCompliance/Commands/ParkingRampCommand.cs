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
    /// 1. The user either selects model lines/arcs already drawn along the ramp
    ///    path (in the direction of travel going up), or draws the path directly
    ///    in the command by clicking points. The path can be the left edge,
    ///    right edge or centerline of the ramp — chosen in the input dialog.
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
        /// Lets the user choose how to define the path: select existing model
        /// lines/arcs (multiple, in any order — they are chained automatically),
        /// or draw the path now by clicking points. Returns null on cancel.
        /// </summary>
        private static RampPath? AcquirePath(UIDocument uiDoc, Document doc)
        {
            var choice = new TaskDialog("Parking Ramp")
            {
                MainInstruction = "How do you want to define the ramp path?",
                MainContent =
                    "The path is the ramp's left edge, right edge or centerline in plan, " +
                    "in the direction of travel going up the ramp.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                AllowCancellation = true
            };
            choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Select drawn model lines",
                "Pick one or more model lines/arcs that form the path. " +
                "Pick the line at the ramp start first — its drawn direction sets the direction of travel.");
            choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Draw the path now",
                "Click points in the view to define a straight-segment path. Press Esc to finish.");
            TaskDialogResult result = choice.Show();

            if (result == TaskDialogResult.CommandLink1)
            {
                IList<Reference> refs;
                try
                {
                    refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        new RampLineSelectionFilter(),
                        "Select the model lines/arcs forming the ramp path (first pick = ramp start), " +
                        "then press Finish.");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return null;
                }

                var curves = new List<Curve>();
                foreach (Reference r in refs)
                    if (doc.GetElement(r) is CurveElement ce && ce.GeometryCurve != null)
                        curves.Add(ce.GeometryCurve);
                if (curves.Count == 0)
                    return null;
                return RampPath.FromCurves(curves);
            }

            if (result == TaskDialogResult.CommandLink2)
            {
                EnsureWorkPlane(uiDoc, doc);
                var points = new List<XYZ>();
                while (true)
                {
                    try
                    {
                        string prompt = points.Count == 0
                            ? "Click the ramp start point (direction of travel goes up the ramp). Esc to finish."
                            : $"Click the next path point ({points.Count} so far). Esc to finish.";
                        points.Add(uiDoc.Selection.PickPoint(prompt));
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        break;
                    }
                }
                if (points.Count < 2)
                    return null;
                return RampPath.FromPoints(points);
            }

            return null;
        }

        /// <summary>PickPoint needs an active work plane; set a horizontal one at the view's level.</summary>
        private static void EnsureWorkPlane(UIDocument uiDoc, Document doc)
        {
            View view = uiDoc.ActiveView;
            if (view.SketchPlane != null)
                return;
            double z = (view as ViewPlan)?.GenLevel?.Elevation ?? 0;
            using var t = new Transaction(doc, "Set work plane");
            t.Start();
            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z));
            view.SketchPlane = SketchPlane.Create(doc, plane);
            t.Commit();
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
