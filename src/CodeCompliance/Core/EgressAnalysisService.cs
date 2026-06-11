using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;

namespace CodeCompliance.Core
{
    /// <summary>One door crossed by an egress path, with its fire rating.</summary>
    public class DoorOnPath
    {
        public ElementId DoorId { get; set; } = ElementId.InvalidElementId;
        public string Mark { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string FireRating { get; set; } = "";
    }

    /// <summary>The result of analysing one egress path (room -> escape stair).</summary>
    public class EgressPathResult
    {
        public ElementId PathId { get; set; } = ElementId.InvalidElementId;
        public string RoomName { get; set; } = "";
        public string LevelName { get; set; } = "";
        public double LengthMeters { get; set; }
        public List<DoorOnPath> Doors { get; } = new List<DoorOnPath>();
    }

    /// <summary>
    /// Creates and analyses Revit "Path of Travel" elements for egress checking:
    /// from the most remote point of each room to the nearest escape stair,
    /// routed automatically around walls and through doors.
    /// </summary>
    public static class EgressAnalysisService
    {
        /// <summary>Mark prefix identifying paths created by this plugin.</summary>
        public const string PathMarkPrefix = "CC - ";

        /// <summary>Doors within this distance of the path polyline count as "crossed" (feet).</summary>
        private const double DoorOnPathToleranceFt = 3.0;

        /// <summary>Offset from a room boundary corner toward the room interior (feet),
        /// so the path start point does not sit inside a wall.</summary>
        private const double BoundaryInsetFt = 0.5;

        /// <summary>
        /// Makes sure the route analysis ignores doors so paths of travel pass through
        /// them instead of treating them as obstacles. Call inside a transaction.
        /// </summary>
        public static void EnsureDoorsArePassable(Document doc)
        {
            try
            {
                RouteAnalysisSettings settings = RouteAnalysisSettings.GetRouteAnalysisSettings(doc);
                var ignored = new List<ElementId>(settings.GetIgnoredCategoryIds());
                var doorsId = new ElementId(BuiltInCategory.OST_Doors);
                if (!ignored.Contains(doorsId))
                {
                    ignored.Add(doorsId);
                    settings.SetIgnoredCategoryIds(ignored);
                }
            }
            catch (Exception)
            {
                // Settings unavailable in some document states; Revit's default route
                // analysis settings already treat doors as passable, so continue.
            }
        }

        /// <summary>Deletes paths previously created by this plugin in the given view.</summary>
        public static int DeleteExistingPaths(Document doc, View view)
        {
            var stale = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_PathOfTravelLines)
                .WhereElementIsNotElementType()
                .Where(IsPluginPath)
                .Select(p => p.Id)
                .ToList();
            foreach (ElementId id in stale)
                doc.Delete(id);
            return stale.Count;
        }

        /// <summary>All paths created by this plugin anywhere in the document.</summary>
        public static IList<Element> CollectPluginPaths(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PathOfTravelLines)
                .WhereElementIsNotElementType()
                .Where(IsPluginPath)
                .ToList();
        }

        public static bool IsPluginPath(Element path)
        {
            string mark = path.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
            return mark.StartsWith(PathMarkPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Creates a path of travel from the most remote point of the room to the given
        /// target point. Returns null when Revit cannot route the path.
        /// </summary>
        public static PathOfTravel? CreatePathFromRoom(ViewPlan view, Room room, XYZ target)
        {
            XYZ start = GetMostRemoteRoomPoint(room, target, view.GenLevel.Elevation);
            PathOfTravel? path = TryCreate(view, start, target);

            // Fall back to the room's own location point if the remote corner is unroutable.
            if (path == null && room.Location is LocationPoint lp)
            {
                XYZ center = new XYZ(lp.Point.X, lp.Point.Y, view.GenLevel.Elevation);
                path = TryCreate(view, center, target);
            }

            if (path != null)
                path.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(PathMarkPrefix + room.Name);
            return path;
        }

        private static PathOfTravel? TryCreate(ViewPlan view, XYZ start, XYZ end)
        {
            try
            {
                return PathOfTravel.Create(view, start, end);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The room boundary corner farthest (straight-line) from the target point,
        /// pulled slightly toward the room interior so it is not inside a wall.
        /// This approximates the "most remote point" of the room.
        /// </summary>
        public static XYZ GetMostRemoteRoomPoint(Room room, XYZ target, double z)
        {
            XYZ center = (room.Location as LocationPoint)?.Point ?? target;
            XYZ best = center;
            double bestDistance = -1;

            var options = new SpatialElementBoundaryOptions();
            IList<IList<BoundarySegment>>? loops = room.GetBoundarySegments(options);
            if (loops != null)
            {
                foreach (IList<BoundarySegment> loop in loops)
                {
                    foreach (BoundarySegment segment in loop)
                    {
                        XYZ p = segment.GetCurve().GetEndPoint(0);
                        double d = Distance2D(p, target);
                        if (d > bestDistance)
                        {
                            bestDistance = d;
                            best = p;
                        }
                    }
                }
            }

            // Inset toward the room center so the point lands in free space.
            XYZ toCenter = new XYZ(center.X - best.X, center.Y - best.Y, 0);
            if (toCenter.GetLength() > BoundaryInsetFt)
            {
                XYZ dir = toCenter.Normalize();
                best = best + dir * BoundaryInsetFt;
            }

            return new XYZ(best.X, best.Y, z);
        }

        /// <summary>Candidate destination points for a stair: its plan center, then the
        /// location of the door nearest to it (often the stair door).</summary>
        public static IList<XYZ> GetStairTargetPoints(Document doc, Element stair, double z)
        {
            var targets = new List<XYZ>();
            BoundingBoxXYZ? box = stair.get_BoundingBox(null);
            if (box != null)
            {
                XYZ c = (box.Min + box.Max) * 0.5;
                targets.Add(new XYZ(c.X, c.Y, z));
            }

            if (targets.Count > 0)
            {
                XYZ stairCenter = targets[0];
                FamilyInstance? nearestDoor = CollectDoors(doc)
                    .Where(d => d.Location is LocationPoint)
                    .OrderBy(d => Distance2D(((LocationPoint)d.Location!).Point, stairCenter))
                    .FirstOrDefault();
                if (nearestDoor != null)
                {
                    XYZ dp = ((LocationPoint)nearestDoor.Location!).Point;
                    targets.Add(new XYZ(dp.X, dp.Y, z));
                }
            }

            return targets;
        }

        public static IList<FamilyInstance> CollectDoors(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();
        }

        /// <summary>Path length from its geometry, converted to meters.</summary>
        public static double GetPathLengthMeters(Element path)
        {
            double feet = 0;
            foreach (Curve curve in GetPathCurves(path))
                feet += curve.Length;
            return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
        }

        /// <summary>Doors whose location lies on (within tolerance of) the path polyline.</summary>
        public static List<FamilyInstance> FindDoorsOnPath(Element path, IEnumerable<FamilyInstance> doors)
        {
            var polyline = new List<XYZ>();
            foreach (Curve curve in GetPathCurves(path))
                polyline.AddRange(curve.Tessellate());

            var result = new List<FamilyInstance>();
            if (polyline.Count < 2)
                return result;

            foreach (FamilyInstance door in doors)
            {
                if (door.Location is not LocationPoint lp)
                    continue;
                XYZ dp = lp.Point;
                double min = double.MaxValue;
                for (int i = 0; i < polyline.Count - 1; i++)
                {
                    double d = DistancePointToSegment2D(dp, polyline[i], polyline[i + 1]);
                    if (d < min)
                        min = d;
                }
                if (min <= DoorOnPathToleranceFt)
                    result.Add(door);
            }
            return result;
        }

        /// <summary>Fire rating of a door, from the instance or its type. "Not rated" when empty.</summary>
        public static string GetDoorFireRating(Document doc, FamilyInstance door)
        {
            Parameter? p = door.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING)
                           ?? door.LookupParameter("Fire Rating");
            if (p == null || !p.HasValue || string.IsNullOrWhiteSpace(p.AsString()))
            {
                Element? type = doc.GetElement(door.GetTypeId());
                p = type?.get_Parameter(BuiltInParameter.DOOR_FIRE_RATING)
                    ?? type?.LookupParameter("Fire Rating");
            }
            string value = (p != null && p.HasValue ? p.AsString() : null) ?? "";
            return string.IsNullOrWhiteSpace(value) ? "Not rated" : value.Trim();
        }

        public static DoorOnPath DescribeDoor(Document doc, FamilyInstance door)
        {
            return new DoorOnPath
            {
                DoorId = door.Id,
                Mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "",
                TypeName = doc.GetElement(door.GetTypeId())?.Name ?? "",
                FireRating = GetDoorFireRating(doc, door)
            };
        }

        private static IList<Curve> GetPathCurves(Element path)
        {
            var curves = new List<Curve>();
            GeometryElement? geometry = path.get_Geometry(new Options());
            if (geometry == null)
                return curves;
            foreach (GeometryObject obj in geometry)
            {
                if (obj is Curve curve)
                    curves.Add(curve);
            }
            return curves;
        }

        public static double Distance2D(XYZ a, XYZ b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double DistancePointToSegment2D(XYZ p, XYZ a, XYZ b)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y;
            double apx = p.X - a.X, apy = p.Y - a.Y;
            double lengthSq = abx * abx + aby * aby;
            double t = lengthSq < 1e-9 ? 0 : Math.Max(0, Math.Min(1, (apx * abx + apy * aby) / lengthSq));
            double cx = a.X + t * abx, cy = a.Y + t * aby;
            double dx = p.X - cx, dy = p.Y - cy;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
