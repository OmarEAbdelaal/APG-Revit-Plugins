using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace CodeCompliance.Core.Annotation
{
    /// <summary>
    /// Places room, door, window and wall tags using each category's default loaded
    /// tag family. Elements already tagged in the view are skipped, and every new tag
    /// asks the occupancy map for a clash-free spot before it is placed.
    /// Paper sizes below are estimates of a typical tag footprint on the sheet; they
    /// only drive clash avoidance, not the tag graphics.
    /// </summary>
    internal static class TagService
    {
        private const double TagWidthMm = 10;
        private const double TagHeightMm = 5;
        private const double RoomTagWidthMm = 16;
        private const double RoomTagHeightMm = 8;
        private const double TagOffsetMm = 5; // distance from the element to the tag

        public static void AddRoomTags(AnnotationContext ctx)
        {
            var taggedRooms = new HashSet<long>();
            var existingTags = new FilteredElementCollector(ctx.Doc, ctx.View.Id).OfClass(typeof(RoomTag));
            foreach (RoomTag tag in existingTags.Cast<RoomTag>())
                if (tag.Room != null)
                    taggedRooms.Add(tag.Room.Id.Value);

            var rooms = new FilteredElementCollector(ctx.Doc, ctx.View.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => r.Area > 0);

            int failures = 0;
            foreach (Room room in rooms)
            {
                if (taggedRooms.Contains(room.Id.Value))
                    continue;
                if (!(room.Location is LocationPoint location))
                    continue;
                try
                {
                    XYZ point = ctx.Occupancy.FindFree(location.Point, RoomTagWidthMm, RoomTagHeightMm);
                    RoomTag tag = ctx.Doc.Create.NewRoomTag(
                        new LinkElementId(room.Id), new UV(point.X, point.Y), ctx.View.Id);
                    ctx.Track(tag, "Room tags");
                }
                catch
                {
                    failures++;
                }
            }
            if (failures > 0)
                ctx.Result.Warn(failures + " room(s) could not be tagged.");
        }

        public static void AddDoorTags(AnnotationContext ctx)
        {
            TagCategory(ctx, BuiltInCategory.OST_Doors, "Door tags",
                "No door tag was placed — make sure a Door Tag family is loaded in the project.");
        }

        public static void AddWindowTags(AnnotationContext ctx)
        {
            TagCategory(ctx, BuiltInCategory.OST_Windows, "Window tags",
                "No window tag was placed — make sure a Window Tag family is loaded in the project.");
        }

        public static void AddWallTags(AnnotationContext ctx)
        {
            TagCategory(ctx, BuiltInCategory.OST_Walls, "Wall tags",
                "No wall tag was placed — make sure a Wall Tag family is loaded in the project.");
        }

        private static void TagCategory(
            AnnotationContext ctx, BuiltInCategory category, string kind, string missingFamilyHint)
        {
            HashSet<long> alreadyTagged = CollectTaggedIds(ctx);

            var elements = new FilteredElementCollector(ctx.Doc, ctx.View.Id)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToElements();

            int placed = 0;
            int failures = 0;
            foreach (Element element in elements)
            {
                if (alreadyTagged.Contains(element.Id.Value))
                    continue;
                XYZ? anchor = TagAnchor(ctx, element);
                if (anchor == null)
                    continue;
                try
                {
                    XYZ point = ctx.Occupancy.FindFree(anchor, TagWidthMm, TagHeightMm);
                    bool needsLeader = point.DistanceTo(anchor) > ctx.Mm(TagOffsetMm * 1.5);
                    IndependentTag tag = IndependentTag.Create(
                        ctx.Doc, ctx.View.Id, new Reference(element), needsLeader,
                        TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point);
                    ctx.Track(tag, kind);
                    placed++;
                }
                catch
                {
                    failures++;
                }
            }
            if (failures > 0 && placed == 0)
                ctx.Result.Warn(missingFamilyHint);
            else if (failures > 0)
                ctx.Result.Warn(failures + " element(s) skipped while placing " + kind.ToLowerInvariant() + ".");
        }

        /// <summary>Ids of elements that already carry a tag in this view.</summary>
        private static HashSet<long> CollectTaggedIds(AnnotationContext ctx)
        {
            var tagged = new HashSet<long>();
            var tags = new FilteredElementCollector(ctx.Doc, ctx.View.Id).OfClass(typeof(IndependentTag));
            foreach (IndependentTag tag in tags.Cast<IndependentTag>())
            {
                try
                {
                    foreach (ElementId id in tag.GetTaggedLocalElementIds())
                        tagged.Add(id.Value);
                }
                catch
                {
                    // orphaned tag — ignore
                }
            }
            return tagged;
        }

        /// <summary>
        /// Where the tag wants to sit: doors/windows beside their opening (pushed off
        /// the host wall), walls at the midpoint of their location line, pushed to the
        /// exterior side.
        /// </summary>
        private static XYZ? TagAnchor(AnnotationContext ctx, Element element)
        {
            double offset = ctx.Mm(TagOffsetMm);

            if (element is FamilyInstance instance && instance.Location is LocationPoint point)
            {
                XYZ direction = (instance.Host as Wall)?.Orientation ?? ctx.Up;
                return point.Point + direction * offset;
            }

            if (element is Wall wall && wall.Location is LocationCurve curve)
            {
                XYZ mid = curve.Curve.Evaluate(0.5, true);
                XYZ direction = wall.Orientation;
                return mid + direction * (wall.Width / 2 + offset);
            }

            if (element.Location is LocationPoint fallback)
                return fallback.Point;
            return null;
        }
    }
}
