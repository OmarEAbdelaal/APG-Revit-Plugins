using System;
using System.Collections.Generic;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// Severity classes used by Dubai Municipality's own QA/QC checker report.
    /// </summary>
    public enum DmSeverity
    {
        /// <summary>Must be resolved before the submission can be approved.</summary>
        Critical = 0,
        /// <summary>Non-critical but expected to be fixed before submission.</summary>
        Error = 1,
        /// <summary>Low priority; fix where feasible.</summary>
        Warning = 2,
        /// <summary>Nothing to do — the check passed.</summary>
        Pass = 3
    }

    /// <summary>The audit phase a finding belongs to (mirrors DM's self-assessment checklist).</summary>
    public enum DmCheckGroup
    {
        ProjectInformation = 0,
        Levels = 1,
        SpacesAndUnits = 2,
        ElementAttributes = 3,
        ObjectNaming = 4,
        GeoReferencing = 5,
        ExportReadiness = 6
    }

    /// <summary>What kind of change fixes a finding — the "type of modification".</summary>
    public enum DmFixKind
    {
        /// <summary>Fill a parameter value on existing elements.</summary>
        SetParameter = 0,
        /// <summary>Load / bind the DM shared parameters to the categories.</summary>
        BindParameter = 1,
        /// <summary>Rename an element, level, room or family type.</summary>
        Rename = 2,
        /// <summary>Change the model itself (place, enclose, re-host, delete elements).</summary>
        ModelChange = 3,
        /// <summary>Change a project-wide setting (units, coordinates, export setup).</summary>
        ProjectSetup = 4,
        /// <summary>Needs a human decision before anything is changed.</summary>
        Review = 5
    }

    /// <summary>
    /// One compliance issue: what is wrong, which elements are affected, what kind of change
    /// fixes it, and the ready-made prompt that asks Claude to fix it over the Revit MCP link.
    /// Findings are grouped per check and per category so the dashboard stays readable even on
    /// models with tens of thousands of elements.
    /// </summary>
    public sealed class DmFinding
    {
        public DmCheckGroup Group { get; set; }
        public DmSeverity Severity { get; set; } = DmSeverity.Error;

        /// <summary>What was checked, e.g. "Walls", "Levels", "Project Information".</summary>
        public string Scope { get; set; } = "";

        /// <summary>One-line statement of the problem.</summary>
        public string Title { get; set; } = "";

        /// <summary>Longer explanation, including why DM requires it.</summary>
        public string Detail { get; set; } = "";

        /// <summary>Clause / rule the requirement comes from.</summary>
        public string Reference { get; set; } = "";

        public DmFixKind FixKind { get; set; } = DmFixKind.SetParameter;

        /// <summary>The concrete change to make, in the modeller's words.</summary>
        public string FixAction { get; set; } = "";

        /// <summary>DM attribute involved, when the fix is a parameter value.</summary>
        public string ParameterName { get; set; } = "";

        /// <summary>Example value from the DM standard (Appendix B "Data Sample").</summary>
        public string SampleValue { get; set; } = "";

        /// <summary>Revit element ids the finding applies to (may be empty for project-level findings).</summary>
        public List<long> ElementIds { get; } = new List<long>();

        /// <summary>Human-readable identification of the first affected elements.</summary>
        public List<string> ElementLabels { get; } = new List<string>();

        /// <summary>How many elements of the scope failed.</summary>
        public int AffectedCount { get; set; }

        /// <summary>How many elements of the scope were examined.</summary>
        public int CheckedCount { get; set; }

        /// <summary>Prompt to paste into Claude with the Revit MCP connector running.</summary>
        public string McpPrompt { get; set; } = "";

        public bool HasElements => ElementIds.Count > 0;

        public string SeverityText
        {
            get
            {
                switch (Severity)
                {
                    case DmSeverity.Critical: return "CRITICAL";
                    case DmSeverity.Error: return "ERROR";
                    case DmSeverity.Warning: return "WARNING";
                    default: return "PASS";
                }
            }
        }

        public string GroupText => GroupName(Group);

        public string FixKindText
        {
            get
            {
                switch (FixKind)
                {
                    case DmFixKind.SetParameter: return "Set parameter";
                    case DmFixKind.BindParameter: return "Load / bind parameter";
                    case DmFixKind.Rename: return "Rename";
                    case DmFixKind.ModelChange: return "Model change";
                    case DmFixKind.ProjectSetup: return "Project setup";
                    default: return "Review";
                }
            }
        }

        public static string GroupName(DmCheckGroup group)
        {
            switch (group)
            {
                case DmCheckGroup.ProjectInformation: return "1. Project / Site / Building";
                case DmCheckGroup.Levels: return "2. Levels (IfcBuildingStorey)";
                case DmCheckGroup.SpacesAndUnits: return "3. Rooms, spaces and units";
                case DmCheckGroup.ElementAttributes: return "4. Element attributes (Appendix B + IDS)";
                case DmCheckGroup.ObjectNaming: return "5. Object and family naming";
                case DmCheckGroup.GeoReferencing: return "6. Geo-referencing and units";
                default: return "7. Export readiness";
            }
        }
    }

    /// <summary>One line of the "checks run" table: a check and how it ended.</summary>
    public sealed class DmCheckSummary
    {
        public DmCheckGroup Group { get; set; }
        public string Name { get; set; } = "";
        public string Result { get; set; } = "";
        public DmSeverity Worst { get; set; } = DmSeverity.Pass;
        public int Issues { get; set; }
        public int Checked { get; set; }
    }

    /// <summary>Everything one audit run produced.</summary>
    public sealed class DmAuditResult
    {
        public string ModelTitle { get; set; } = "";
        public string ModelPath { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string ProjectNumber { get; set; } = "";
        public DmPermitStage Stage { get; set; } = DmPermitStage.Final;
        public bool IncludeConditional { get; set; }
        public DateTime RunAt { get; set; } = DateTime.Now;
        public string RevitVersion { get; set; } = "";
        public string KnowledgeBaseSource { get; set; } = "";

        public List<DmFinding> Findings { get; } = new List<DmFinding>();
        public List<DmCheckSummary> Checks { get; } = new List<DmCheckSummary>();

        public int Count(DmSeverity severity)
        {
            int n = 0;
            foreach (DmFinding finding in Findings)
            {
                if (finding.Severity == severity)
                    n++;
            }
            return n;
        }

        public int AffectedElements
        {
            get
            {
                var ids = new HashSet<long>();
                foreach (DmFinding finding in Findings)
                {
                    foreach (long id in finding.ElementIds)
                        ids.Add(id);
                }
                return ids.Count;
            }
        }

        /// <summary>
        /// Submission readiness in percent: how many of the executed checks came back clean,
        /// with critical findings weighted three times an error and six times a warning.
        /// </summary>
        public int ReadinessPercent
        {
            get
            {
                if (Checks.Count == 0)
                    return 0;
                double penalty = Count(DmSeverity.Critical) * 3.0 + Count(DmSeverity.Error) * 1.0 +
                                 Count(DmSeverity.Warning) * 0.5;
                double budget = Checks.Count * 1.5;
                double score = 100.0 * (1.0 - Math.Min(1.0, penalty / Math.Max(1.0, budget)));
                return (int)Math.Round(score);
            }
        }
    }
}
