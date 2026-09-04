using System.Collections.Generic;
using System.Text;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// Minimal RFC 4180 CSV reader. The Dubai Municipality knowledge-base files (Appendix B
    /// element attribute tables and Appendix C usage codes) contain quoted fields with commas
    /// and embedded line breaks, so splitting on ',' is not enough.
    /// </summary>
    internal static class DmCsv
    {
        public static List<List<string>> Parse(string text)
        {
            var rows = new List<List<string>>();
            if (string.IsNullOrEmpty(text))
                return rows;

            var row = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        row.Add(field.ToString().Trim());
                        field.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(field.ToString().Trim());
                        field.Clear();
                        rows.Add(row);
                        row = new List<string>();
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            }

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString().Trim());
                rows.Add(row);
            }

            return rows;
        }

        /// <summary>Value of column <paramref name="index"/> or "" when the row is shorter.</summary>
        public static string Cell(IList<string> row, int index)
        {
            return index >= 0 && index < row.Count ? row[index] : "";
        }

        /// <summary>True when every cell of the row is empty.</summary>
        public static bool IsEmpty(IList<string> row)
        {
            foreach (string cell in row)
            {
                if (!string.IsNullOrWhiteSpace(cell))
                    return false;
            }
            return true;
        }
    }
}
