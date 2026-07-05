using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Section B.7.2.2, Tables B.9 / B.10) from a model line the user draws:
    /// 1. The user draws a model line (straight ramp) or model arc (curved/helical
    ///    ramp) in a plan view, in the direction of travel going up, then runs
    ///    this command and selects it. The line can be the left edge, the right
    ///    edge or the centerline of the ramp — chosen in the input dialog.
    /// 2. The dialog asks for two of the three key parameters (floor height h,
    ///    slope S, total run R) and solves for the third, exactly like the
    ///    standalone Parking Ramp Calculator app, with code compliance checked
    ///    live at the input step. Non-compliant designs cannot be created.
    /// 3. The ramp (entry transition + main run + exit transition) is created
    ///    as a DirectShape in the Ramps category.
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

            // ── Step 1: the user selects the drawn line that indicates the ramp ──
            Reference pickedRef;
            try
            {
                pickedRef = uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    new RampLineSelectionFilter(),
                    "Select the model line (straight ramp) or model arc (curved/helical ramp) " +
                    "drawn in the direction of travel, then the input dialog opens. Press Esc to cancel.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                TaskDialog.Show("Parking Ramp",
                    "No line selected.\n\n" +
                    "Draw a model line (straight ramp) or a model arc (curved / helical ramp) " +
                    "in a plan view at the ramp start level, in the direction of travel going up, " +
                    "then run this command again and select it.");
                return Result.Cancelled;
            }

            if (!(doc.GetElement(pickedRef) is CurveElement curveElement) || curveElement.GeometryCurve == null)
            {
                message = "The selected element is not a usable line.";
                return Result.Failed;
            }
            Curve drawnCurve = curveElement.GeometryCurve;

            RampPathInfo path;
            try
            {
                path = RampGeometryBuilder.AnalyzePath(drawnCurve);
            }
            catch (RampCalcException ex)
            {
                TaskDialog.Show("Parking Ramp", ex.Message);
                return Result.Cancelled;
            }

            // ── Step 2: parameters + live code compliance ────────────────────────
            var window = new RampInputWindow(path);
            window.ShowDialog();
            if (!window.Confirmed || window.Result == null)
                return Result.Cancelled;

            RampCalcResult calc = window.Result;

            // ── Step 3: build the geometry and create the DirectShape ───────────
            IList<GeometryObject> shape;
            try
            {
                shape = RampGeometryBuilder.Build(
                    drawnCurve, calc, window.TotalWidth, window.SlabThickness, window.Location);
            }
            catch (Exception ex)
            {
                message = "Failed to build the ramp geometry: " + ex.Message;
                return Result.Failed;
            }

            long createdId;
            using (var t = new Transaction(doc, "Create parking ramp"))
            {
                t.Start();
                DirectShape ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_Ramps));
                ds.SetShape(shape);
                ds.SetName($"CC - Parking Ramp ({calc.Type})");

                Parameter mark = ds.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (mark != null && !mark.IsReadOnly)
                    mark.Set("CC - Ramp");

                Parameter comments = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (comments != null && !comments.IsReadOnly)
                    comments.Set(BuildSummary(calc, window));

                createdId = ds.Id.Value;
                t.Commit();
            }

            TaskDialog.Show("Parking Ramp",
                $"Ramp created (element id {createdId}).\n\n" +
                BuildSummary(calc, window) +
                "\n\nAll Table B.9 checks passed at the input step. " +
                "The full design data is stored in the element's Comments parameter.");
            return Result.Succeeded;
        }

        private static string BuildSummary(RampCalcResult calc, RampInputWindow window)
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Format(ci,
                "Dubai BC Annex B B.7.2.2 | {0} ramp, {1} lane(s) x {2:F2} m | " +
                "h = {3:F3} m, S = {4:F2}%, T = {5:F2}%, X = {6:F2} m, X' = {7:F2} m, R = {8:F3} m | " +
                "slab {9:F2} m, line = {10}",
                calc.Type, window.Lanes, window.LaneWidth,
                calc.H, calc.S, calc.T, calc.X, calc.XPrime, calc.R,
                window.SlabThickness, window.Location);
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
