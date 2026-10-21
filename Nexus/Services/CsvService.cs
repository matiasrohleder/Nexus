using System.Collections.Generic;
using System.Text;

namespace Nexus.Services;

/// <summary>Minimal RFC-4180 CSV reader/writer (quotes, embedded commas/newlines).</summary>
public static class CsvService
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(ch);
            }
            else
            {
                switch (ch)
                {
                    case '"': inQuotes = true; break;
                    case ',': row.Add(cell.ToString()); cell.Clear(); break;
                    case '\r': break;
                    case '\n':
                        row.Add(cell.ToString()); cell.Clear();
                        if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
                        row.Clear();
                        break;
                    default: cell.Append(ch); break;
                }
            }
        }

        // trailing cell/row without final newline
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
        }

        return rows;
    }

    public static string Escape(string? value)
    {
        if (value is null) return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    public static string BuildLine(IEnumerable<string?> cells)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var cell in cells)
        {
            if (!first) sb.Append(',');
            sb.Append(Escape(cell));
            first = false;
        }
        return sb.ToString();
    }
}
