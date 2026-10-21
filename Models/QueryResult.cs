using System.Collections.Generic;

namespace Nexus.Models;

/// <summary>
/// Represents one row of a result set as an index-addressable set of cell values.
/// Exposing an <c>this[int]</c> indexer lets the results DataGrid bind columns by ordinal
/// even though the schema is only known at runtime.
/// </summary>
public sealed class ResultRow
{
    private readonly object?[] _values;

    public ResultRow(object?[] values) => _values = values;

    public int Count => _values.Length;

    /// <summary>Raw (untruncated, typed) cell value — used by export and the data editor.</summary>
    public object? GetRaw(int index) =>
        index >= 0 && index < _values.Length ? _values[index] : null;

    public string this[int index] =>
        index >= 0 && index < _values.Length ? FormatCell(_values[index]) : string.Empty;

    private static string FormatCell(object? value) => value switch
    {
        null => "(null)",
        System.DBNull => "(null)",
        byte[] bytes => "0x" + System.Convert.ToHexString(bytes),
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? string.Empty
    };
}

/// <summary>The outcome of executing a single SQL statement.</summary>
public sealed class QueryResult
{
    public List<string> Columns { get; } = new();
    public List<ResultRow> Rows { get; } = new();

    /// <summary>Rows affected for non-SELECT statements (-1 when not applicable).</summary>
    public int RecordsAffected { get; set; } = -1;

    public double DurationMs { get; set; }
    public string? Error { get; set; }

    /// <summary>Human-readable message shown in the Messages tab (e.g. "42 rows in 12 ms").</summary>
    public string Message { get; set; } = string.Empty;

    public bool HasError => Error is not null;
    public bool HasResultSet => Columns.Count > 0;
}
