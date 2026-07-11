using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace CodeCompliance.Core.Annotation
{
    /// <summary>
    /// The non-dimension, non-tag annotations:
    ///  - spot elevations on the walkable surfaces of stairs and ramps (low + high point),
    ///  - ramp slope text notes ("UP  S = x.x%") at the ramp center — APG-created ramps
    ///    (Mark "CC - Ramp ...") report the exact designed slope from their Comments,
    ///    other ramps get a slope estimated from their bounding box,
    ///  - stair path arrows (Revit's own up/down annotation) for stairs missing one,
    ///  - callout suggestions: spots that deserve a detail callout, reported to the
    ///    user but never created automatically.
    /// </summary>
    internal static class SymbolService
    {
        // ── Spot elevations ─────────────────────────────────────────────────────

        public static void AddSpotElevations(AnnotationContext ctx)
        {
            var targets = new List<Element>();
            targets.AddRange(Collect(ctx, BuiltInCategory.OST_Stairs));
            targets.AddRange(Collect(ctx, BuiltInCategory.OST_Ramps));
            targets.AddRange(CcRampFloors(ctx));

            int failures = 0;
            foreach (Element element in targets)
            {
                try
                {
                    if (!TryAddSpotsOnTopFaces(ctx, element))
                        failures++;
                }
                catch
                {
                    failures++;
                }
            }
            if (failures > 0)
                ctx.Result.Warn(failures + " stair/ramp element(s) offered no usable top surface for a spot elevation.");
        }

        /// <summary>
        /// Spot the lowest and highest walkable (upward-facing) faces of the element,
        /// which marks the arrival and departure levels of a stair or ramp.
        /// </summary>
        private static bool TryAddSpotsOnTopFaces(AnnotationContext ctx, Element element)
        {
            var faces = new List<PlanarFace>();
            var options = new Options { ComputeReferences = true };
            GeometryElement? geometry = element.get_Geometry(options);
            if (geometry == null)
                return false;
            CollectTopFaces(geometry, faces);
            if (faces.Count == 0)
                return false;

            faces.Sort((a, b) => a.Origin.Z.CompareTo(b.Origin.Z));
            var picks = new List<PlanarFace> { faces.First() };
            if (faces.Count > 1 && faces.Last().Origin.Z - faces.First().Origin.Z > 0.1)
                picks.Add(faces.Last());

            bool any = false;
            foreach (PlanarFace face in picks)
            {
                XYZ? point = FacePoint(face);
                if (point == null || face.Reference == null)
                    continue;
                XYZ placed = ctx.Occupancy.FindFree(point, 12, 5);
                SpotDimension spot = ctx.Doc.Create.NewSpotElevation(
                    ctx.View, face.Reference, point, placed, placed, point, true);
                ctx.Track(spot, "Spot elevations");
                any = true;
            }
            return any;
        }

        private static void CollectTopFaces(GeometryElement geometry, List<PlanarFace> faces)
        {
            foreach (GeometryObject obj in geometry)
            {
                if (obj is Solid solid && !solid.Faces.IsEmpty)
                {
                    foreach (Face face in solid.Faces)
                        if (face is PlanarFace planar && planar.FaceNormal.Z > 0.7 && planar.Area > 0.5)
                            faces.Add(planar);
                }
                else if (obj is GeometryInstance instance)
                {
                    CollectTopFaces(instance.GetInstanceGeometry(), faces);
                }
            }
        }

        /// <summary>A point safely inside the face (center of its UV box, verified).</summary>
        private static XYZ? FacePoint(Face face)
        {
            BoundingBoxUV box = face.GetBoundingBox();
            var center = new UV((box.Min.U + box.Max.U) / 2, (box.Min.V + box.Max.V) / 2);
            if (face.IsInside(center))
                return face.Evaluate(center);
            // Off-center faces (L-shaped landings): probe a small grid for an inside point.
            for (double u = 0.25; u <= 0.75; u += 0.25)
            {
                for (double v = 0.25; v <= 0.75; v += 0.25)
                {
                    var probe = new UV(
                        box.Min.U + (box.Max.U - box.Min.U) * u,
                        box.Min.V + (box.Max.V - box.Min.V) * v);
                    if (face.IsInside(probe))
                        return face.Evaluate(probe);
                }
            }
            return null;
        }

        // ── Ramp slope notes ────────────────────────────────────────────────────

        public static void AddRampSlopeNotes(AnnotationContext ctx)
        {
            ElementId textTypeId = ctx.Doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (textTypeId == ElementId.InvalidElementId)
            {
                ctx.Result.Warn("No text note type exists, so ramp slope notes were skipped.");
                return;
            }

            foreach (Element ramp in Collect(ctx, BuiltInCategory.OST_Ramps))
                AddSlopeNote(ctx, ramp, textTypeId, SlopeFromBoundingBox(ctx, ramp));

            foreach (Element floor in CcRampFloors(ctx))
                AddSlopeNote(ctx, floor, textTypeId,
                    SlopeFromComments(floor) ?? SlopeFromBoundingBox(ctx, floor));
        }

        private static void AddSlopeNote(AnnotationContext ctx, Element element, ElementId textTypeId, string? slope)
        {
            if (slope == null)
                return;
            BoundingBoxXYZ? box = element.get_BoundingBox(ctx.View);
            if (box == null)
                return;
            try
            {
                XYZ center = (box.Min + box.Max) / 2;
                XYZ point = ctx.Occupancy.FindFree(center, 20, 5);
                TextNote note = TextNote.Create(ctx.Doc, ctx.View.Id, point, "UP  " + slope, textTypeId);
                ctx.Track(note, "Ramp slope notes");
            }
            catch
            {
                ctx.Result.Warn("A ramp slope note could not be placed.");
            }
        }

        /// <summary>APG ramps store "S = x.xx%" in Comments — the designed slope, exact.</summary>
        private static string? SlopeFromComments(Element element)
        {
            string comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString() ?? "";
            Match match = Regex.Match(comments, @"S = ([\d.]+)%");
            return match.Success ? "S = " + match.Groups[1].Value + "%" : null;
        }

        /// <summary>Rise over the longer plan run of the bounding box — an estimate, marked so.</summary>
        private static string? SlopeFromBoundingBox(AnnotationContext ctx, Element element)
        {
            BoundingBoxXYZ? box = element.get_BoundingBox(null);
            if (box == null)
                return null;
            double rise = box.Max.Z - box.Min.Z;
            double run = Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y);
            if (run < 0.5 || rise < 0.05)
                return null;
            double percent = rise / run * 100;
            return "S ≈ " + percent.ToString("F1", CultureInfo.InvariantCulture) + "%";
        }

        // ── Stair path arrows ───────────────────────────────────────────────────

        public static void AddStairPaths(AnnotationContext ctx)
        {
            ElementId pathTypeId = new FilteredElementCollector(ctx.Doc)
                .OfClass(typeof(StairsPathType))
                .FirstElementId();
            if (pathTypeId == ElementId.InvalidElementId)
            {
                ctx.Result.Warn("No stair path type exists in the project, so stair arrows were skipped.");
                return;
            }

            var stairs = new FilteredElementCollector(ctx.Doc, ctx.View.Id)
                .OfCategory(BuiltInCategory.OST_Stairs)
                .WhereElementIsNotElementType()
                .OfType<Stairs>();

            var pathFilter = new ElementClassFilter(typeof(StairsPath));
            int failures = 0;
            foreach (Stairs stair in stairs)
            {
                // Skip stairs that already show a path arrow in this view.
                bool hasPath = stair.GetDependentElements(pathFilter)
                    .Select(id => ctx.Doc.GetElement(id))
                    .OfType<StairsPath>()
                    .Any(p => p.OwnerViewId == ctx.View.Id);
                if (hasPath)
                    continue;
                try
                {
                    StairsPath path = StairsPath.Create(
                        ctx.Doc, new LinkElementId(stair.Id), pathTypeId, ctx.View.Id);
                    ctx.Track(path, "Stair path arrows");
                }
                catch
                {
                    failures++;
                }
            }
            if (failures > 0)
                ctx.Result.Warn(failures + " stair(s) could not get a path arrow (in-place or group members?).");
        }

        // ── Callout suggestions (advisory only) ─────────────────────────────────

        private static readonly string[] CalloutKeywords =
        {
            "toilet", "wc", "bath", "kitchen", "lift", "elevator", "stair", "pantry", "shaft"
        };

        public static void SuggestCallouts(AnnotationContext ctx)
        {
            foreach (Element stair in Collect(ctx, BuiltInCategory.OST_Stairs))
                ctx.Result.CalloutSuggestions.Add("Stair: " + DisplayName(stair));
            foreach (Element ramp in Collect(ctx, BuiltInCategory.OST_Ramps))
                ctx.Result.CalloutSuggestions.Add("Ramp: " + DisplayName(ramp));
            foreach (Element floor in CcRampFloors(ctx))
                ctx.Result.CalloutSuggestions.Add("Ramp: " + DisplayName(floor));

            var rooms = new FilteredElementCollector(ctx.Doc, ctx.View.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>();
            foreach (Room room in rooms)
            {
                string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                string lower = name.ToLowerInvariant();
                if (CalloutKeywords.Any(k => lower.Contains(k)))
                    ctx.Result.CalloutSuggestions.Add("Room: " + name + " " +
                        (room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? ""));
            }
        }

        private static string DisplayName(Element element)
        {
            string mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
            return string.IsNullOrEmpty(mark) ? element.Name : mark;
        }

        // ── Shared collectors ───────────────────────────────────────────────────

        private static List<Element> Collect(AnnotationContext ctx, BuiltInCategory category)
        {
            return new FilteredElementCollector(ctx.Doc, ctx.View.Id)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToElements()
                .ToList();
        }

        /// <summary>Ramps built by the APG Ramp Creator plugin (floors marked "CC - Ramp ...").</summary>
        private static List<Element> CcRampFloors(AnnotationContext ctx)
        {
            return new FilteredElementCollector(ctx.Doc, ctx.View.Id)
                .OfClass(typeof(Floor))
                .Where(f => (f.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "")
                    .StartsWith("CC - Ramp", StringComparison.Ordinal))
                .ToList();
        }
    }
}
