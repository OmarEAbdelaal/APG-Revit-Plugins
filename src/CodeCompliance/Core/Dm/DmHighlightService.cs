using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CodeCompliance.Core.Dm
{
    /// <summary>Result of preparing a 3D view for a finding.</summary>
    public sealed class DmHighlightResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public ElementId ViewId { get; set; } = ElementId.InvalidElementId;
        public List<ElementId> Elements { get; } = new List<ElementId>();
    }

    /// <summary>
    /// Puts the elements of a finding in front of the user: a dedicated 3D view whose section
    /// box is fitted around them, the elements coloured red in that view and selected, so the
    /// problem is visible in context instead of as a list of ids.
    ///
    /// It is called from the dashboard's external event, so it runs in a Revit API context and
    /// can open the view immediately — the dashboard stays open next to it and Revit keeps
    /// working normally.
    /// </summary>
    public static class DmHighlightService
    {
        /// <summary>Name of the 3D view the plugin creates and reuses.</summary>
        public const string ViewName = "CC - DM Compliance 3D";

        private static readonly List<ElementId> Highlighted = new List<ElementId>();

        /// <summary>Margin around the elements, in metres.</summary>
        private const double MarginMeters = 1.5;

        public static DmHighlightResult Show(UIDocument uiDoc, IEnumerable<long> elementIds)
        {
            var result = new DmHighlightResult();
            Document doc = uiDoc.Document;

            var ids = new List<ElementId>();
            BoundingBoxXYZ? bounds = null;
            foreach (long raw in elementIds)
            {
                var id = new ElementId(raw);
                Element? element = doc.GetElement(id);
                if (element == null)
                    continue;
                ids.Add(id);
                bounds = Union(bounds, SafeBoundingBox(element));
            }

            if (ids.Count == 0)
            {
                result.Message = "None of the elements of this finding exists in the model any more.";
                return result;
            }

            result.Elements.AddRange(ids);

            if (bounds == null)
            {
                // Levels, project information and similar have no geometry: select them only.
                try
                {
                    uiDoc.Selection.SetElementIds(ids);
                    uiDoc.ShowElements(ids);
                }
                catch
                {
                    // an element that cannot be shown in the active view is still selected
                }
                result.Success = true;
                result.Message = ids.Count + " element(s) selected. They have no 3D geometry, so no section " +
                                 "box was created.";
                return result;
            }

            double margin = UnitUtils.ConvertToInternalUnits(MarginMeters, UnitTypeId.Meters);
            var box = new BoundingBoxXYZ
            {
                Min = new XYZ(bounds.Min.X - margin, bounds.Min.Y - margin, bounds.Min.Z - margin),
                Max = new XYZ(bounds.Max.X + margin, bounds.Max.Y + margin, bounds.Max.Z + margin)
            };

            using (var transaction = new Transaction(doc, "DM compliance – highlight elements"))
            {
                transaction.Start();

                View3D? view = FindOrCreateView(doc);
                if (view == null)
                {
                    transaction.RollBack();
                    result.Message = "No 3D view type is available in this project, so the section box view " +
                                     "could not be created.";
                    return result;
                }

                ClearPrevious(doc, view);

                view.IsSectionBoxActive = true;
                view.SetSectionBox(box);

                ShowRoomsIfNeeded(doc, view, ids);

                OverrideGraphicSettings overrides = RedOverride(doc);
                foreach (ElementId id in ids)
                {
                    try
                    {
                        view.SetElementOverrides(id, overrides);
                        Highlighted.Add(id);
                    }
                    catch
                    {
                        // elements that cannot be overridden in a 3D view (rooms in some
                        // configurations) are still inside the section box
                    }
                }

                result.ViewId = view.Id;
                transaction.Commit();
            }

            bool opened = false;
            try
            {
                // The dashboard is modeless and this runs inside its external event, so the
                // view can be activated straight away instead of waiting for the window to close.
                if (doc.GetElement(result.ViewId) is View3D view3D)
                {
                    if (uiDoc.ActiveView == null || uiDoc.ActiveView.Id != view3D.Id)
                        uiDoc.ActiveView = view3D;
                    opened = true;
                }
                uiDoc.Selection.SetElementIds(ids);
                uiDoc.ShowElements(ids);
            }
            catch
            {
                // Revit refuses to change the active view while a sketch or another modal
                // operation is running: the colours and the section box are set either way.
            }

            result.Success = true;
            result.Message = ids.Count + " element(s) framed in \"" + ViewName + "\" and coloured red" +
                             (opened
                                 ? ". The view is open — Revit stays usable, the dashboard stays where it is."
                                 : ". Open \"" + ViewName + "\" to see them (Revit was busy).");
            return result;
        }

        /// <summary>Removes the plugin's colour overrides from the compliance view.</summary>
        public static void Clear(Document doc)
        {
            if (Highlighted.Count == 0)
                return;
            View3D? view = FindView(doc);
            if (view == null)
            {
                Highlighted.Clear();
                return;
            }
            using (var transaction = new Transaction(doc, "DM compliance – clear highlight"))
            {
                transaction.Start();
                ClearPrevious(doc, view);
                transaction.Commit();
            }
        }

        private static void ClearPrevious(Document doc, View3D view)
        {
            var empty = new OverrideGraphicSettings();
            foreach (ElementId id in Highlighted)
            {
                try
                {
                    if (doc.GetElement(id) != null)
                        view.SetElementOverrides(id, empty);
                }
                catch
                {
                    // ignore elements that were deleted meanwhile
                }
            }
            Highlighted.Clear();
        }

        private static View3D? FindView(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, ViewName, StringComparison.Ordinal));
        }

        private static View3D? FindOrCreateView(Document doc)
        {
            View3D? existing = FindView(doc);
            if (existing != null)
                return existing;

            ViewFamilyType? viewType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (viewType == null)
                return null;

            View3D view = View3D.CreateIsometric(doc, viewType.Id);
            try
            {
                view.Name = ViewName;
            }
            catch
            {
                // a view with that name may exist as a template: keep the generated name
            }
            try
            {
                view.DetailLevel = ViewDetailLevel.Fine;
                view.DisplayStyle = DisplayStyle.ShadingWithEdges;
            }
            catch
            {
                // display settings are cosmetic
            }
            return view;
        }

        private static void ShowRoomsIfNeeded(Document doc, View3D view, List<ElementId> ids)
        {
            bool hasRooms = ids.Any(id =>
            {
                Element? element = doc.GetElement(id);
                return element?.Category != null &&
                       element.Category.Id.Value == (long)BuiltInCategory.OST_Rooms;
            });
            if (!hasRooms)
                return;

            try
            {
                var rooms = new ElementId(BuiltInCategory.OST_Rooms);
                if (view.CanCategoryBeHidden(rooms))
                    view.SetCategoryHidden(rooms, false);
            }
            catch
            {
                // rooms are not displayable in 3D in every configuration
            }
        }

        private static OverrideGraphicSettings RedOverride(Document doc)
        {
            var overrides = new OverrideGraphicSettings();
            var red = new Color(200, 30, 30);
            overrides.SetProjectionLineColor(red);
            overrides.SetCutLineColor(red);
            overrides.SetProjectionLineWeight(6);

            ElementId solidFill = SolidFillPatternId(doc);
            if (solidFill != ElementId.InvalidElementId)
            {
                overrides.SetSurfaceForegroundPatternId(solidFill);
                overrides.SetSurfaceForegroundPatternColor(new Color(230, 90, 90));
                overrides.SetSurfaceForegroundPatternVisible(true);
                overrides.SetCutForegroundPatternId(solidFill);
                overrides.SetCutForegroundPatternColor(red);
                overrides.SetCutForegroundPatternVisible(true);
            }
            return overrides;
        }

        private static ElementId SolidFillPatternId(Document doc)
        {
            FillPatternElement? solid = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
            return solid?.Id ?? ElementId.InvalidElementId;
        }

        private static BoundingBoxXYZ? SafeBoundingBox(Element element)
        {
            try
            {
                return element.get_BoundingBox(null);
            }
            catch
            {
                return null;
            }
        }

        private static BoundingBoxXYZ? Union(BoundingBoxXYZ? first, BoundingBoxXYZ? second)
        {
            if (second == null)
                return first;
            if (first == null)
                return second;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Min(first.Min.X, second.Min.X),
                              Math.Min(first.Min.Y, second.Min.Y),
                              Math.Min(first.Min.Z, second.Min.Z)),
                Max = new XYZ(Math.Max(first.Max.X, second.Max.X),
                              Math.Max(first.Max.Y, second.Max.Y),
                              Math.Max(first.Max.Z, second.Max.Z))
            };
        }
    }
}
