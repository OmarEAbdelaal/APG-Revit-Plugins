using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CodeCompliance.Core.Dm;

namespace CodeCompliance.Reporting
{
    /// <summary>
    /// Writes the DM BIM compliance audit to three files under Documents\CodeCompliance:
    /// an HTML dashboard for reading and sharing, a CSV of every finding with its element ids
    /// (for tracking the fixes), and a text file with the Revit MCP prompts.
    /// </summary>
    public static class DmComplianceReportWriter
    {
        public static (string HtmlPath, string CsvPath, string PromptPath) Write(DmAuditResult result)
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CodeCompliance");
            Directory.CreateDirectory(folder);

            string stamp = result.RunAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string safeTitle = string.Concat(result.ModelTitle.Split(Path.GetInvalidFileNameChars()));
            string htmlPath = Path.Combine(folder, safeTitle + "_DM_Compliance_" + stamp + ".html");
            string csvPath = Path.Combine(folder, safeTitle + "_DM_Compliance_" + stamp + ".csv");
            string promptPath = Path.Combine(folder, safeTitle + "_DM_Compliance_Prompts_" + stamp + ".txt");

            File.WriteAllText(htmlPath, BuildHtml(result), Encoding.UTF8);
            File.WriteAllText(csvPath, BuildCsv(result), Encoding.UTF8);
            File.WriteAllText(promptPath, BuildPrompts(result), Encoding.UTF8);
            return (htmlPath, csvPath, promptPath);
        }

        private static string BuildCsv(DmAuditResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Severity,Check group,Scope,Issue,Fix type,Parameter,Fix action,Affected,Checked,DM reference,Element ids");
            foreach (DmFinding finding in Ordered(result))
            {
                sb.AppendLine(string.Join(",",
                    Csv(finding.SeverityText),
                    Csv(finding.GroupText),
                    Csv(finding.Scope),
                    Csv(finding.Title),
                    Csv(finding.FixKindText),
                    Csv(finding.ParameterName),
                    Csv(finding.FixAction),
                    finding.AffectedCount.ToString(CultureInfo.InvariantCulture),
                    finding.CheckedCount.ToString(CultureInfo.InvariantCulture),
                    Csv(finding.Reference),
                    Csv(string.Join(" ", finding.ElementIds))));
            }
            return sb.ToString();
        }

        private static string BuildPrompts(DmAuditResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Revit MCP prompts — DM BIM compliance");
            sb.AppendLine("Model: " + result.ModelTitle);
            sb.AppendLine("Generated: " + result.RunAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            sb.AppendLine(new string('=', 78));
            sb.AppendLine();
            sb.AppendLine("### Whole-audit prompt");
            sb.AppendLine();
            sb.AppendLine(DmPromptBuilder.ForAudit(result));
            sb.AppendLine();

            int index = 1;
            foreach (DmFinding finding in Ordered(result))
            {
                sb.AppendLine(new string('=', 78));
                sb.AppendLine("### " + index + ". [" + finding.SeverityText + "] " + finding.Scope + " — " + finding.Title);
                sb.AppendLine();
                sb.AppendLine(finding.McpPrompt);
                sb.AppendLine();
                index++;
            }
            return sb.ToString();
        }

        private static string BuildHtml(DmAuditResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.AppendLine("<title>DM BIM Compliance – " + Html(result.ModelTitle) + "</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:0;color:#232833;background:#f4f5fa}");
            sb.AppendLine(".band{background:linear-gradient(180deg,#2726A9,#171666);color:#fff;padding:20px 28px}");
            sb.AppendLine(".band h1{margin:0;font-size:21px} .band p{margin:4px 0 0;opacity:.8;font-size:13px}");
            sb.AppendLine(".wrap{padding:20px 28px 40px}");
            sb.AppendLine(".cards{display:flex;flex-wrap:wrap;gap:12px;margin-bottom:18px}");
            sb.AppendLine(".card{background:#fff;border:1px solid #dddfe8;border-radius:6px;padding:12px 16px;min-width:130px}");
            sb.AppendLine(".card .n{font-size:24px;font-weight:600} .card .l{font-size:11px;color:#6d7485;text-transform:uppercase}");
            sb.AppendLine("h2{font-size:15px;margin:26px 0 6px;color:#171666}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;background:#fff}");
            sb.AppendLine("th,td{border:1px solid #dddfe8;padding:6px 9px;text-align:left;font-size:12.5px;vertical-align:top}");
            sb.AppendLine("th{background:#eef0f7;font-size:11px;text-transform:uppercase;color:#4a5165}");
            sb.AppendLine(".crit{color:#c0392b;font-weight:700} .err{color:#c47a12;font-weight:600} .warn{color:#6d7485}");
            sb.AppendLine(".pass{color:#1e8449;font-weight:600}");
            sb.AppendLine("details{margin-top:6px} summary{cursor:pointer;color:#2726A9;font-size:12px}");
            sb.AppendLine("pre{white-space:pre-wrap;background:#f7f8fc;border:1px solid #e3e5ef;border-radius:4px;padding:8px;font-size:11.5px}");
            sb.AppendLine(".ids{font-family:Consolas,monospace;font-size:11px;color:#4a5165;word-break:break-all}");
            sb.AppendLine("footer{margin-top:30px;font-size:11.5px;color:#6d7485}");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine("<div class='band'><h1>DM BIM Compliance report</h1><p>" +
                          Html(result.ModelTitle) + " &middot; Dubai BIM Standard " + DmKnowledgeBase.StandardVersion +
                          " &middot; " + (result.Stage == DmPermitStage.Final ? "Final permit" : "Preliminary permit") +
                          " &middot; Revit " + Html(result.RevitVersion) +
                          " &middot; " + result.RunAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                          "</p></div><div class='wrap'>");

            sb.AppendLine("<div class='cards'>");
            sb.AppendLine(Card(result.Count(DmSeverity.Critical).ToString(CultureInfo.InvariantCulture), "Critical", "crit"));
            sb.AppendLine(Card(result.Count(DmSeverity.Error).ToString(CultureInfo.InvariantCulture), "Errors", "err"));
            sb.AppendLine(Card(result.Count(DmSeverity.Warning).ToString(CultureInfo.InvariantCulture), "Warnings", "warn"));
            sb.AppendLine(Card(result.AffectedElements.ToString(CultureInfo.InvariantCulture), "Elements to modify", ""));
            sb.AppendLine(Card(result.ReadinessPercent.ToString(CultureInfo.InvariantCulture) + "%", "Submission readiness", ""));
            sb.AppendLine("</div>");

            sb.AppendLine("<h2>Checks run</h2>");
            sb.AppendLine("<table><tr><th>Phase</th><th>Check</th><th>Examined</th><th>Result</th></tr>");
            foreach (DmCheckSummary check in result.Checks)
            {
                sb.AppendLine("<tr><td>" + Html(DmFinding.GroupName(check.Group)) + "</td><td>" + Html(check.Name) +
                              "</td><td>" + check.Checked + "</td><td class='" + CssClass(check.Worst) + "'>" +
                              Html(check.Result) + "</td></tr>");
            }
            sb.AppendLine("</table>");

            foreach (IGrouping<DmCheckGroup, DmFinding> group in Ordered(result).GroupBy(f => f.Group).OrderBy(g => (int)g.Key))
            {
                sb.AppendLine("<h2>" + Html(DmFinding.GroupName(group.Key)) + "</h2>");
                sb.AppendLine("<table><tr><th>Severity</th><th>Scope</th><th>Issue</th><th>Type of modification</th>" +
                              "<th>Affected</th><th>What to do</th></tr>");
                foreach (DmFinding finding in group)
                {
                    sb.AppendLine("<tr>");
                    sb.AppendLine("<td class='" + CssClass(finding.Severity) + "'>" + finding.SeverityText + "</td>");
                    sb.AppendLine("<td>" + Html(finding.Scope) + "</td>");
                    sb.AppendLine("<td><b>" + Html(finding.Title) + "</b><br><span style='color:#6d7485'>" +
                                  Html(finding.Detail) + "</span><br><span style='color:#8a90a1;font-size:11px'>" +
                                  Html(finding.Reference) + "</span>" + ElementBlock(finding) + "</td>");
                    sb.AppendLine("<td>" + Html(finding.FixKindText) +
                                  (finding.ParameterName.Length > 0 ? "<br><code>" + Html(finding.ParameterName) + "</code>" : "") +
                                  "</td>");
                    sb.AppendLine("<td>" + finding.AffectedCount + (finding.CheckedCount > 0 ? " / " + finding.CheckedCount : "") + "</td>");
                    sb.AppendLine("<td>" + Html(finding.FixAction) +
                                  "<details><summary>Revit MCP prompt</summary><pre>" + Html(finding.McpPrompt) + "</pre></details></td>");
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("</table>");
            }

            sb.AppendLine("<h2>Fix the whole audit with Claude</h2>");
            sb.AppendLine("<pre>" + Html(DmPromptBuilder.ForAudit(result)) + "</pre>");

            sb.AppendLine("<footer>Generated by APG Revit Plugins &middot; DM BIM Compliance. Rules loaded from " +
                          Html(result.KnowledgeBaseSource) + " (Dubai Municipality IDS rule set, Appendix B element " +
                          "attribute matrices and Appendix C usage codes). This report anticipates the IFC export: " +
                          "compliance is finally assessed by DM on the exported IFC file.</footer>");
            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        private static string ElementBlock(DmFinding finding)
        {
            if (!finding.HasElements)
                return "";
            var sb = new StringBuilder();
            sb.Append("<details><summary>").Append(finding.ElementIds.Count)
              .Append(" element id(s)</summary><div class='ids'>");
            sb.Append(string.Join(", ", finding.ElementIds.Take(2000)));
            if (finding.ElementIds.Count > 2000)
                sb.Append(" … (" + (finding.ElementIds.Count - 2000) + " more in the CSV)");
            sb.Append("</div></details>");
            return sb.ToString();
        }

        private static IEnumerable<DmFinding> Ordered(DmAuditResult result)
        {
            return result.Findings
                .OrderBy(f => (int)f.Severity)
                .ThenBy(f => (int)f.Group)
                .ThenByDescending(f => f.AffectedCount);
        }

        private static string Card(string number, string label, string cssClass)
        {
            return "<div class='card'><div class='n " + cssClass + "'>" + number + "</div><div class='l'>" +
                   Html(label) + "</div></div>";
        }

        private static string CssClass(DmSeverity severity)
        {
            switch (severity)
            {
                case DmSeverity.Critical: return "crit";
                case DmSeverity.Error: return "err";
                case DmSeverity.Warning: return "warn";
                default: return "pass";
            }
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        private static string Html(string value)
        {
            return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
