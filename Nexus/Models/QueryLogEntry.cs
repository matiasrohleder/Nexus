using System;

namespace Nexus.Models;

/// <summary>The origin of a profiled query.</summary>
public enum QuerySource
{
    /// <summary>Executed by Nexus itself (this app).</summary>
    Application,
    /// <summary>Observed running on the server via pg_stat_activity (any client).</summary>
    ServerActivity
}

/// <summary>One entry in the Profiler timeline — a query that was executed or observed.</summary>
public sealed class QueryLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public QuerySource Source { get; init; } = QuerySource.Application;
    public string Connection { get; init; } = string.Empty;
    public string Database { get; init; } = string.Empty;
    public string Sql { get; init; } = string.Empty;

    /// <summary>Duration in milliseconds (application queries) or backend state age (server activity).</summary>
    public double DurationMs { get; init; }

    public int RowCount { get; init; } = -1;
    public bool Success { get; init; } = true;
    public string? Error { get; init; }

    /// <summary>Stable identity for server-activity dedup (backend pid + query_start). Empty for app queries.</summary>
    public string DedupKey { get; init; } = string.Empty;

    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");
    public string SourceText => Source == QuerySource.Application ? "App" : "Server";
    public string DurationText => DurationMs >= 0 ? $"{DurationMs:0.0} ms" : "—";
    public string RowCountText => RowCount >= 0 ? RowCount.ToString() : "—";
    public string StatusText => Success ? "OK" : "ERROR";

    /// <summary>Single-line preview of the SQL for the grid.</summary>
    public string SqlPreview
    {
        get
        {
            var s = Sql.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length > 160 ? s[..160] + " …" : s;
        }
    }
}
