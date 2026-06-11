using System.Linq;
using Autodesk.Revit.DB;

namespace CodeCompliance.Reporting
{
    /// <summary>
    /// Creates the Revit schedules for the egress workflow:
    /// one for the travel paths and one for door fire ratings.
    /// Must be called inside an open transaction.
    /// </summary>
    public static class ScheduleBuilder
    {
        public const string PathScheduleName = "CC - Egress Travel Paths";
        public const string DoorScheduleName = "CC - Door Fire Ratings";

        public static void EnsureSchedules(Document doc)
        {
            if (!ScheduleExists(doc, PathScheduleName))
            {
                ViewSchedule paths = ViewSchedule.CreateSchedule(
                    doc, new ElementId(BuiltInCategory.OST_PathOfTravelLines));
                paths.Name = PathScheduleName;
                AddFields(doc, paths, "Mark", "Level", "Length", "Time", "From Room", "To Room");
            }

            if (!ScheduleExists(doc, DoorScheduleName))
            {
                ViewSchedule doors = ViewSchedule.CreateSchedule(
                    doc, new ElementId(BuiltInCategory.OST_Doors));
                doors.Name = DoorScheduleName;
                AddFields(doc, doors, "Mark", "Family and Type", "Level", "Width", "Fire Rating");
            }
        }

        private static bool ScheduleExists(Document doc, string name)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Any(v => v.Name == name);
        }

        private static void AddFields(Document doc, ViewSchedule schedule, params string[] fieldNames)
        {
            var available = schedule.Definition.GetSchedulableFields();
            foreach (string name in fieldNames)
            {
                SchedulableField? field = available.FirstOrDefault(f => f.GetName(doc) == name);
                if (field == null)
                    continue;
                try
                {
                    schedule.Definition.AddField(field);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Field already present or not applicable - skip.
                }
            }
        }
    }
}
