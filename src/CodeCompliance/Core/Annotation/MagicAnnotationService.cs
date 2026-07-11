using Autodesk.Revit.DB;

namespace CodeCompliance.Core.Annotation
{
    /// <summary>
    /// Runs one Magic Annotation pass over a view: optionally clears what the previous
    /// run created, seeds the occupancy map with the annotations already present, then
    /// lets each ticked service place its annotations. Callers wrap this in a single
    /// transaction so the whole pass is one undo step.
    /// </summary>
    internal static class MagicAnnotationService
    {
        public static AnnotationResult Run(Document doc, View view, AnnotationOptions options)
        {
            var ctx = new AnnotationContext(doc, view, options);

            if (options.ReplaceExisting)
                ctx.Result.Removed = AnnotationRegistry.DeletePrevious(doc, view);

            ctx.Occupancy.SeedFromExisting();

            bool isPlan = view is ViewPlan;

            if (isPlan && (options.GridDimensions || options.OverallDimensions))
                DimensionService.AddGridDimensions(ctx);
            if (isPlan && options.OpeningDimensions)
                DimensionService.AddOpeningDimensions(ctx);
            if (!isPlan && options.LevelDimensions)
                DimensionService.AddLevelDimensions(ctx);

            if (options.RoomTags)
                TagService.AddRoomTags(ctx);
            if (options.DoorTags)
                TagService.AddDoorTags(ctx);
            if (options.WindowTags)
                TagService.AddWindowTags(ctx);
            if (options.WallTags)
                TagService.AddWallTags(ctx);

            if (isPlan && options.SpotElevations)
                SymbolService.AddSpotElevations(ctx);
            if (isPlan && options.RampSlopeNotes)
                SymbolService.AddRampSlopeNotes(ctx);
            if (isPlan && options.StairPaths)
                SymbolService.AddStairPaths(ctx);

            if (options.SuggestCallouts)
                SymbolService.SuggestCallouts(ctx);

            AnnotationRegistry.Save(doc, view, ctx.Created);
            return ctx.Result;
        }
    }
}
