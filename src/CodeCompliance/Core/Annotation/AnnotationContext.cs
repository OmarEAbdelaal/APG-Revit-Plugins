using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core.Annotation
{
    /// <summary>
    /// Everything the annotation services share during one run: the document and view,
    /// the ticked options, the result being built, the ids created so far (for the
    /// registry) and the occupancy map used to keep tags from overlapping.
    ///
    /// Distances are expressed in "paper millimetres" — the size the annotation has on
    /// the printed sheet — and converted to model feet with the view scale, so the same
    /// offsets stay readable at 1:50 and 1:200 alike.
    /// </summary>
    internal class AnnotationContext
    {
        public Document Doc { get; }
        public View View { get; }
        public AnnotationOptions Options { get; }
        public AnnotationResult Result { get; }
        public List<ElementId> Created { get; } = new List<ElementId>();
        public OccupancyMap Occupancy { get; }

        /// <summary>View-plane basis (unit vectors in model space).</summary>
        public XYZ Right { get; }
        public XYZ Up { get; }
        public XYZ Normal { get; }

        public AnnotationContext(Document doc, View view, AnnotationOptions options)
        {
            Doc = doc;
            View = view;
            Options = options;
            Result = new AnnotationResult();
            Right = view.RightDirection;
            Up = view.UpDirection;
            Normal = Right.CrossProduct(Up);
            Occupancy = new OccupancyMap(this);
        }

        /// <summary>Paper millimetres on the sheet → model feet at this view's scale.</summary>
        public double Mm(double paperMm)
        {
            return paperMm * View.Scale / 304.8;
        }

        /// <summary>Remember an element this run created (for cleanup on the next run).</summary>
        public void Track(Element? element, string kind)
        {
            if (element == null)
                return;
            Created.Add(element.Id);
            Result.Add(kind);
        }

        /// <summary>Projection of a model point onto the view plane axes.</summary>
        public void ToPlane(XYZ point, out double x, out double y)
        {
            x = point.DotProduct(Right);
            y = point.DotProduct(Up);
        }
    }

    /// <summary>
    /// Greedy 2D occupancy map in view-plane coordinates. Seeded with the bounding
    /// boxes of annotations already in the view; each new tag reserves a rectangle,
    /// and candidates that collide are nudged to the first free position.
    /// </summary>
    internal class OccupancyMap
    {
        private readonly AnnotationContext _ctx;
        private readonly List<double[]> _rects = new List<double[]>(); // xMin, yMin, xMax, yMax

        public OccupancyMap(AnnotationContext ctx)
        {
            _ctx = ctx;
        }

        /// <summary>Reserve the view bounding boxes of annotations already in the view.</summary>
        public void SeedFromExisting()
        {
            var types = new List<Type>
            {
                typeof(IndependentTag),
                typeof(TextNote),
                typeof(Dimension),
                typeof(SpotDimension),
                typeof(Autodesk.Revit.DB.Architecture.RoomTag)
            };
            foreach (Type type in types)
            {
                var collector = new FilteredElementCollector(_ctx.Doc, _ctx.View.Id).OfClass(type);
                foreach (Element element in collector)
                    ReserveElement(element);
            }
        }

        /// <summary>Reserve the rectangle around a model-space center point.</summary>
        public void Reserve(XYZ center, double widthFt, double heightFt)
        {
            _ctx.ToPlane(center, out double x, out double y);
            _rects.Add(new[] { x - widthFt / 2, y - heightFt / 2, x + widthFt / 2, y + heightFt / 2 });
        }

        /// <summary>Reserve an element's actual bounding box in this view, if it has one.</summary>
        public void ReserveElement(Element element)
        {
            BoundingBoxXYZ? box = element.get_BoundingBox(_ctx.View);
            if (box == null)
                return;
            _ctx.ToPlane(box.Min, out double x0, out double y0);
            _ctx.ToPlane(box.Max, out double x1, out double y1);
            _rects.Add(new[]
            {
                Math.Min(x0, x1), Math.Min(y0, y1),
                Math.Max(x0, x1), Math.Max(y0, y1)
            });
        }

        private bool IsFree(XYZ center, double widthFt, double heightFt)
        {
            _ctx.ToPlane(center, out double x, out double y);
            double xMin = x - widthFt / 2, yMin = y - heightFt / 2;
            double xMax = x + widthFt / 2, yMax = y + heightFt / 2;
            foreach (double[] r in _rects)
                if (xMin < r[2] && xMax > r[0] && yMin < r[3] && yMax > r[1])
                    return false;
            return true;
        }

        /// <summary>
        /// First clash-free position for a tag of the given paper size: the preferred
        /// point itself, then rings of nudges around it. Falls back to the preferred
        /// point when everything is congested (a readable-but-overlapping tag beats
        /// no tag at all).
        /// </summary>
        public XYZ FindFree(XYZ preferred, double paperWidthMm, double paperHeightMm)
        {
            double w = _ctx.Mm(paperWidthMm);
            double h = _ctx.Mm(paperHeightMm);

            foreach (XYZ candidate in Candidates(preferred))
            {
                if (IsFree(candidate, w, h))
                {
                    Reserve(candidate, w, h);
                    return candidate;
                }
            }
            Reserve(preferred, w, h);
            return preferred;
        }

        private IEnumerable<XYZ> Candidates(XYZ preferred)
        {
            yield return preferred;
            double[] steps = { 4, 8, 12 }; // paper mm
            foreach (double stepMm in steps)
            {
                double s = _ctx.Mm(stepMm);
                yield return preferred + _ctx.Up * s;
                yield return preferred - _ctx.Up * s;
                yield return preferred + _ctx.Right * s;
                yield return preferred - _ctx.Right * s;
                yield return preferred + (_ctx.Right + _ctx.Up) * (s * 0.7);
                yield return preferred + (_ctx.Right - _ctx.Up) * (s * 0.7);
                yield return preferred - (_ctx.Right + _ctx.Up) * (s * 0.7);
                yield return preferred - (_ctx.Right - _ctx.Up) * (s * 0.7);
            }
        }
    }
}
