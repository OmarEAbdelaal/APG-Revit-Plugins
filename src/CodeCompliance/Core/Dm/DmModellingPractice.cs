using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// One of Dubai Municipality's recommended modelling practices, as published in the
    /// "Recommended Modelling Practices" material: what the model has to look like, how bad it
    /// is when it does not, what the modeller changes, and the tolerances the check uses.
    ///
    /// Like every other DM rule in this plugin the practice is <b>data</b>
    /// (<c>modelling_practices.json</c>), never C#: the audit code only implements the
    /// detection, so a re-worded, re-graded or retuned practice needs no new build.
    /// </summary>
    public sealed class DmModellingPractice
    {
        /// <summary>Stable id, e.g. "RMP-01" — the audit code keys its detection on it.</summary>
        public string Id { get; set; } = "";

        /// <summary>Whether the practice is checked at all.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Position in the dashboard and in the report.</summary>
        public int Order { get; set; }

        /// <summary>What is being checked, e.g. "Walls" — shown in the Scope column.</summary>
        public string Scope { get; set; } = "";

        /// <summary>One-line statement of the practice.</summary>
        public string Title { get; set; } = "";

        /// <summary>Why DM asks for it, in the modeller's words.</summary>
        public string Requirement { get; set; } = "";

        public DmSeverity Severity { get; set; } = DmSeverity.Warning;
        public DmFixKind FixKind { get; set; } = DmFixKind.ModelChange;

        /// <summary>DM clause the practice comes from.</summary>
        public string Reference { get; set; } = "";

        /// <summary>The change to make, in one sentence.</summary>
        public string FixAction { get; set; } = "";

        /// <summary>The same change as Revit user-interface steps.</summary>
        public string RevitSteps { get; set; } = "";

        /// <summary>What Claude has to be careful about when it applies the fix over MCP.</summary>
        public string McpHint { get; set; } = "";

        /// <summary>Thresholds, keyword lists and category lists the detection reads.</summary>
        public Dictionary<string, object> Settings { get; } =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public double Number(string key, double fallback)
        {
            if (!Settings.TryGetValue(key, out object? value) || value == null)
                return fallback;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        public bool Flag(string key, bool fallback)
        {
            if (!Settings.TryGetValue(key, out object? value) || value == null)
                return fallback;
            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        public string Text(string key, string fallback = "")
        {
            return Settings.TryGetValue(key, out object? value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback
                : fallback;
        }

        public IReadOnlyList<string> List(string key)
        {
            if (!Settings.TryGetValue(key, out object? value) || !(value is List<string> list))
                return new List<string>();
            return list;
        }

        /// <summary>Length setting in millimetres, converted to metres.</summary>
        public double Metres(string key, double fallbackMillimetres)
        {
            return Number(key, fallbackMillimetres) / 1000.0;
        }

        /// <summary>True when the name contains one of the keywords of a setting.</summary>
        public bool Matches(string key, string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            string upper = name.ToUpperInvariant();
            return List(key).Any(keyword => keyword.Length > 0 &&
                                            upper.IndexOf(keyword.ToUpperInvariant(), StringComparison.Ordinal) >= 0);
        }
    }
}
