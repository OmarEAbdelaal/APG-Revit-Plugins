using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CodeCompliance.Core;

namespace CodeCompliance.Reporting
{
    /// <summary>
    /// Writes the egress analysis results to a CSV file (for spreadsheets) and an
    /// HTML file (for reading/printing) under Documents\CodeCompliance.
    /// </summary>
    public static class EgressReportWriter
    {
        public static (string CsvPath, string HtmlPath) Write(string modelTitle, IList<EgressPathResult> results)
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CodeCompliance");
            Directory.CreateDirectory(folder);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string safeTitle = string.Concat(modelTitle.Split(Path.GetInvalidFileNameChars()));
            string csvPath = Path.Combine(folder, $"{safeTitle}_Egress_{stamp}.csv");
            string htmlPath = Path.Combine(folder, $"{safeTitle}_Egress_{stamp}.html");

            File.WriteAllText(csvPath, BuildCsv(results), Encoding.UTF8);
            File.WriteAllText(htmlPath, BuildHtml(modelTitle, results), Encoding.UTF8);
            return (csvPath, htmlPath);
        }

        private static string BuildCsv(IList<EgressPathResult> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Room,Level,Travel Distance (m),Doors Crossed,Door Details (Mark | Type | Fire Rating)");
            foreach (EgressPathResult r in results)
            {
                string doors = string.Join("; ",
                    r.Doors.Select(d => $"{Display(d.Mark)} | {d.TypeName} | {d.FireRating}"));
                sb.AppendLine(string.Join(",",
                    Csv(r.RoomName), Csv(r.LevelName),
                    r.LengthMeters.ToString("F2", CultureInfo.InvariantCulture),
                    r.Doors.Count.ToString(CultureInfo.InvariantCulture),
                    Csv(doors)));
            }
            return sb.ToString();
        }

        private static string BuildHtml(string modelTitle, IList<EgressPathResult> results)
        {
            EgressPathResult? longest = results.OrderByDescending(r => r.LengthMeters).FirstOrDefault();
            int unratedDoors = results.SelectMany(r => r.Doors)
                .GroupBy(d => d.DoorId).Select(g => g.First())
                .Count(d => d.FireRating == "Not rated");

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.AppendLine("<title>Egress Report - " + Html(modelTitle) + "</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222}");
            sb.AppendLine("h1{font-size:20px} h2{font-size:16px;margin-top:24px}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;margin-top:8px}");
            sb.AppendLine("th,td{border:1px solid #bbb;padding:6px 10px;text-align:left;font-size:13px}");
            sb.AppendLine("th{background:#f0f0f0} .warn{color:#b00;font-weight:bold}");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine("<h1>Fire Fighting - Egress Travel Distance Report</h1>");
            sb.AppendLine("<p>Model: <b>" + Html(modelTitle) + "</b><br>");
            sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "<br>");
            sb.AppendLine("Paths analysed: " + results.Count);
            if (longest != null)
                sb.AppendLine("<br>Longest travel distance: <b>" +
                              longest.LengthMeters.ToString("F2", CultureInfo.InvariantCulture) +
                              " m</b> (" + Html(longest.RoomName) + ")");
            if (unratedDoors > 0)
                sb.AppendLine("<br><span class='warn'>" + unratedDoors +
                              " door(s) on escape routes have no fire rating.</span>");
            sb.AppendLine("</p>");

            sb.AppendLine("<h2>Travel paths (room to escape stair)</h2>");
            sb.AppendLine("<table><tr><th>Room</th><th>Level</th><th>Travel distance (m)</th><th>Doors crossed</th></tr>");
            foreach (EgressPathResult r in results.OrderByDescending(x => x.LengthMeters))
            {
                sb.AppendLine("<tr><td>" + Html(r.RoomName) + "</td><td>" + Html(r.LevelName) +
                              "</td><td>" + r.LengthMeters.ToString("F2", CultureInfo.InvariantCulture) +
                              "</td><td>" + r.Doors.Count + "</td></tr>");
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<h2>Doors on escape routes</h2>");
            sb.AppendLine("<table><tr><th>Door mark</th><th>Type</th><th>Fire rating</th><th>On path of room</th></tr>");
            foreach (EgressPathResult r in results)
            {
                foreach (DoorOnPath d in r.Doors)
                {
                    string rating = d.FireRating == "Not rated"
                        ? "<span class='warn'>Not rated</span>"
                        : Html(d.FireRating);
                    sb.AppendLine("<tr><td>" + Html(Display(d.Mark)) + "</td><td>" + Html(d.TypeName) +
                                  "</td><td>" + rating + "</td><td>" + Html(r.RoomName) + "</td></tr>");
                }
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<p style='margin-top:24px;font-size:12px;color:#666'>" +
                          "Generated by Code Compliance - Fire Fighting Revit plugin. " +
                          "Travel distances follow Revit's Path of Travel routing from the most remote " +
                          "room point to the nearest escape stair.</p>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static string Display(string mark) => string.IsNullOrWhiteSpace(mark) ? "(no mark)" : mark;

        private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        private static string Html(string value) =>
            value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
