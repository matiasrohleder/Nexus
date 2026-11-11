using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Models;

/// <summary>
/// A grid row whose cells can be read as text by ordinal: query results (<see cref="ResultRow"/>)
/// and data-editor rows. Lets the shared grid code size columns, detect JSON/XML and open values
/// without caring which grid it is.
/// </summary>
public interface ICellSource
{
    /// <summary>The cell's full text; null = SQL NULL.</summary>
    string? GetText(int index);
}

/// <summary>
/// Represents one row of a result set as an index-addressable set of cell values.
/// Exposing an <c>this[int]</c> indexer lets the results DataGrid bind columns by ordinal
/// even though the schema is only known at runtime.
/// </summary>
public sealed class ResultRow : ICellSource
{
    private readonly object?[] _values;

    public ResultRow(object?[] values) => _values = values;

    public int Count => _values.Length;

    /// <summary>Raw (untruncated, typed) cell value — used by export and the data editor.</summary>
    public object? GetRaw(int index) =>
        index >= 0 && index < _values.Length ? _values[index] : null;

    public string this[int index] =>
        index >= 0 && index < _values.Length ? FormatCell(_values[index]) : string.Empty;

    public string? GetText(int index) =>
        GetRaw(index) is null or System.DBNull ? null : this[index];

    private static string FormatCell(object? value) => value switch
    {
        null => "(null)",
        System.DBNull => "(null)",
        byte[] bytes => "0x" + System.Convert.ToHexString(bytes),
        bool b => b ? "true" : "false",
        string s => s,
        IEnumerable list => CellText.FormatList(list, v => FormatCell(v)),
        _ => value.ToString() ?? string.Empty
    };
}

/// <summary>Text conventions shared by the results grid and the data editor.</summary>
public static class CellText
{
    public const string NullText = "(null)";

    /// <summary>
    /// Arrays/lists (PostgreSQL arrays, DuckDB/ClickHouse lists) as a PostgreSQL array literal,
    /// <c>{a,"b c",NULL}</c> — readable everywhere and round-trips through the PG data editor.
    /// (Their default ToString() is just the CLR type name, e.g. "System.String[]".)
    /// </summary>
    public static string FormatList(IEnumerable list, System.Func<object?, string?> element)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var item in list)
        {
            if (!first) sb.Append(',');
            first = false;
            if (item is null or System.DBNull) { sb.Append("NULL"); continue; }
            var text = item is IEnumerable nested and not string and not byte[]
                ? FormatList(nested, element)
                : element(item) ?? "NULL";
            bool quote = item is string && (text.Length == 0 || text.Equals("NULL", System.StringComparison.OrdinalIgnoreCase)
                || text.IndexOfAny(new[] { ',', '{', '}', '"', '\\', ' ', '\t', '\n' }) >= 0);
            sb.Append(quote ? "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : text);
        }
        return sb.Append('}').ToString();
    }
}

/// <summary>The outcome of executing a single SQL statement.</summary>
public sealed class QueryResult
{
    public List<string> Columns { get; } = new();

    /// <summary>
    /// Engine type name per column (parallel to <see cref="Columns"/>; may be shorter or hold
    /// nulls when the driver doesn't report one). Used to recognise JSON/XML columns.
    /// </summary>
    public List<string?> ColumnTypes { get; } = new();

    public List<ResultRow> Rows { get; } = new();

    /// <summary>Rows affected for non-SELECT statements (-1 when not applicable).</summary>
    public int RecordsAffected { get; set; } = -1;

    /// <summary>True when the reader stopped at the per-result row cap (more rows exist).</summary>
    public bool IsTruncated { get; set; }

    public double DurationMs { get; set; }
    public string? Error { get; set; }

    /// <summary>Human-readable message shown in the Messages tab (e.g. "42 rows in 12 ms").</summary>
    public string Message { get; set; } = string.Empty;

    public bool HasError => Error is not null;
    public bool HasResultSet => Columns.Count > 0;

    public string? ColumnType(int index) => index < ColumnTypes.Count ? ColumnTypes[index] : null;
}
