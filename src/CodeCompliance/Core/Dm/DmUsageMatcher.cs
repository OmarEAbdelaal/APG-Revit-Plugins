using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// Proposes the Appendix C space usage code that fits a room name, so the fix prompt can
    /// carry a ready mapping table for the rooms of this model instead of the whole 185-entry
    /// vocabulary. Suggestions are proposals: DM validates the code literally, so the mapping
    /// is always presented for confirmation before it is written.
    /// </summary>
    public static class DmUsageMatcher
    {
        /// <summary>Best matching space usage code for a room name, or null.</summary>
        public static DmUsageCode? Suggest(string roomName)
        {
            string name = Normalize(roomName);
            if (name.Length == 0)
                return null;

            DmUsageCode? best = null;
            int bestScore = 0;
            foreach (DmUsageCode code in DmKnowledgeBase.SpaceUsageCodes.Values)
            {
                int score = Score(name, Normalize(code.Description));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = code;
                }
            }
            return bestScore >= 3 ? best : null;
        }

        /// <summary>
        /// A "room name → suggested code" table for the distinct names of the given rooms,
        /// ready to be pasted into the prompt.
        /// </summary>
        public static string SuggestionTable(IEnumerable<string> roomNames)
        {
            List<string> names = roomNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0)
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("Suggested Appendix C mapping for the room names found in this model " +
                          "(proposal — confirm before writing):");
            sb.AppendLine("  room name                     ->  SpaceUsageCode   SpaceUsageDescription");
            foreach (string name in names.Take(200))
            {
                DmUsageCode? code = Suggest(name);
                sb.AppendLine("  " + Pad(name, 28) + "  ->  " +
                              (code == null
                                  ? "(no confident match — pick from usage_Space.csv)"
                                  : Pad(code.Code, 15) + "  " + code.Description.Trim()));
            }
            if (names.Count > 200)
                sb.AppendLine("  … " + (names.Count - 200) + " further room names in the CSV report");
            return sb.ToString();
        }

        private static int Score(string name, string description)
        {
            if (description.Length == 0)
                return 0;
            if (name == description)
                return 100;
            if (description.Contains(name) || name.Contains(description))
                return 50 + Math.Min(20, Math.Min(name.Length, description.Length));

            string[] nameWords = name.Split(' ');
            string[] descriptionWords = description.Split(' ');
            int shared = nameWords.Count(w => w.Length > 2 && descriptionWords.Contains(w));
            return shared * 4;
        }

        private static string Normalize(string value)
        {
            var sb = new StringBuilder();
            foreach (char c in (value ?? "").ToUpperInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            return string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string Pad(string value, int width)
        {
            return value.Length >= width ? value.Substring(0, width) : value.PadRight(width);
        }
    }
}
