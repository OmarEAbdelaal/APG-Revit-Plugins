using System.Collections.Generic;

namespace CodeCompliance.Core.Annotation
{
    /// <summary>
    /// What the user ticked in the Magic Annotation checklist. Every run starts from
    /// an explicit selection (nothing is inferred from the view scale by design).
    /// </summary>
    public class AnnotationOptions
    {
        // Dimensions
        public bool OverallDimensions { get; set; }
        public bool GridDimensions { get; set; }
        public bool OpeningDimensions { get; set; }
        public bool LevelDimensions { get; set; }

        // Tags
        public bool RoomTags { get; set; }
        public bool DoorTags { get; set; }
        public bool WindowTags { get; set; }
        public bool WallTags { get; set; }

        // Symbols
        public bool SpotElevations { get; set; }
        public bool RampSlopeNotes { get; set; }
        public bool StairPaths { get; set; }

        // Advisory only — callout views are never created automatically.
        public bool SuggestCallouts { get; set; }

        /// <summary>Delete what the previous Magic Annotation run placed in this view first.</summary>
        public bool ReplaceExisting { get; set; } = true;
    }

    /// <summary>Outcome of one Magic Annotation run, shown in the summary dialog.</summary>
    public class AnnotationResult
    {
        /// <summary>Count of created elements per human-readable kind ("Grid dimensions", ...).</summary>
        public Dictionary<string, int> Counts { get; } = new Dictionary<string, int>();

        /// <summary>Non-fatal problems (missing tag family, unreachable face, ...).</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Places that deserve a callout view (advisory, nothing is created).</summary>
        public List<string> CalloutSuggestions { get; } = new List<string>();

        public int Removed { get; set; }

        public void Add(string kind)
        {
            Counts.TryGetValue(kind, out int n);
            Counts[kind] = n + 1;
        }

        public void Warn(string text)
        {
            if (!Warnings.Contains(text))
                Warnings.Add(text);
        }

        public int Total
        {
            get
            {
                int total = 0;
                foreach (KeyValuePair<string, int> pair in Counts)
                    total += pair.Value;
                return total;
            }
        }
    }
}
