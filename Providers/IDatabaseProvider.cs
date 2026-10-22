using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Models;

namespace Nexus.Providers;

/// <summary>What a given engine can do; drives which UI affordances are shown.</summary>
public sealed record ProviderCapabilities
{
    /// <summary>Engine speaks SQL (editor + grid). False for Mongo/Redis which get their own views.</summary>
    public bool SupportsSql { get; init; } = true;

    /// <summary>Containers are schemas inside the connected database (PG, MSSQL). False = containers are databases (MySQL) or there is a single implicit container (SQLite).</summary>
    public bool SupportsSchemas { get; init; }

    public bool SupportsServerActivity { get; init; }
    public bool SupportsUsers { get; init; }
    public bool SupportsKillSession { get; init; }
    public bool SupportsExplain { get; init; }
    public bool SupportsRowEditing { get; init; } = true;

    /// <summary>File-based engine (SQLite, DuckDB): connection dialog shows a file picker instead of host/port/credentials.</summary>
    public bool IsFileBased { get; init; }

    /// <summary>Executable to look for in PATH for backup/restore (e.g. "pg_dump"); null = no external tool.</summary>
    public string? BackupTool { get; init; }
}

public enum DbObjectKind { Table, View, Collection }

public sealed record DbObjectInfo(string Name, DbObjectKind Kind);

public sealed record ColumnInfo(string Name, string DataType, bool Nullable, bool IsPrimaryKey = false);

public sealed record SessionInfo(string Id, string? User, string? Database, string? State, string? Query, double? DurationMs)
{
    /// <summary>Query collapsed to one line so multi-line SQL doesn't blow up the sessions grid's row height.</summary>
    public string? QueryPreview => Query is null ? null : System.Text.RegularExpressions.Regex.Replace(Query, @"\s+", " ").Trim();
}

public sealed record UserInfo(string Name, bool IsAdmin, string? Details);

public sealed record TableStats(string Name, long RowEstimate, string? TotalSize);

public sealed record ServerVariable(string Name, string Value);

/// <summary>
/// One implementation per engine. All database access in Nexus goes through this contract;
/// UI never talks to a driver directly. Optional areas are capability-gated — callers must
/// check <see cref="Capabilities"/> before invoking them.
/// </summary>
public interface IDatabaseProvider
{
    // ---- identity / connection-dialog metadata ----
    string Id { get; }               // "postgresql"
    string DisplayName { get; }      // "PostgreSQL"
    string Badge { get; }            // two-letter tile/tree badge ("Pg")
    string BrandColor { get; }       // badge background (hex)
    int DefaultPort { get; }
    string DefaultUsername { get; }
    string DefaultDatabase { get; }
    ProviderCapabilities Capabilities { get; }

    // ---- core ----
    Task<(bool ok, string message)> TestConnectionAsync(SavedConnection c, CancellationToken ct = default);

    /// <summary>
    /// Closes every live server connection Nexus holds for this saved connection (pools of the
    /// profile and its database-pinned clones; see <see cref="Services.ConnectionStatus"/>).
    /// The next operation simply reconnects.
    /// </summary>
    Task DisconnectAsync(SavedConnection c);

    /// <summary>Executes a script/command batch. For SQL engines: SQL text. Always logs to the profiler.</summary>
    Task<List<QueryResult>> ExecuteAsync(SavedConnection c, string sql, CancellationToken ct = default);

    /// <summary>
    /// Executes multiple statements inside one transaction (all-or-nothing). Used by the data
    /// editor's Commit and by CSV import. Returns the number of statements executed.
    /// </summary>
    Task<int> ExecuteTransactionAsync(SavedConnection c, IReadOnlyList<string> statements, CancellationToken ct = default);

    // ---- introspection (object explorer) ----
    /// <summary>
    /// Databases hosted on the server, for engines where a connection pins one database but the
    /// server hosts many (PostgreSQL, SQL Server). Empty list = no database level in the tree.
    /// Browsing another database clones the connection with that database name.
    /// </summary>
    Task<List<string>> GetDatabasesAsync(SavedConnection c, CancellationToken ct = default);

    /// <summary>Schemas (PG/MSSQL), databases (MySQL/Mongo) or the single implicit container (SQLite → ["main"]).</summary>
    Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default);
    Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default);
    Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default);

    // ---- dialect helpers ----
    string QuoteIdent(string ident);
    /// <summary>Escapes a string as a single-quoted SQL literal for this dialect.</summary>
    string QuoteLiteral(string value);

    /// <summary>
    /// Command that drops a container (schema for PG/MSSQL, database for MySQL/Mongo/ClickHouse).
    /// Null = the engine does not support dropping containers from Nexus.
    /// </summary>
    string? BuildDropContainerSql(string container, bool cascade);
    /// <summary>
    /// SELECT query in the engine's dialect (used by View Data / double-click-table).
    /// When <paramref name="columns"/> is provided, emits an explicit, line-broken column list
    /// instead of *; falls back to * otherwise.
    /// </summary>
    string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null);
    /// <summary>Prefix that turns a query into a plan request ("EXPLAIN "), or null if unsupported.</summary>
    string? ExplainPrefix { get; }

    // ---- profiler / admin (capability-gated) ----
    Task<List<QueryLogEntry>> GetServerActivityAsync(SavedConnection c, CancellationToken ct = default);
    Task<List<SessionInfo>> GetSessionsAsync(SavedConnection c, CancellationToken ct = default);
    Task KillSessionAsync(SavedConnection c, string sessionId, CancellationToken ct = default);
    Task<List<UserInfo>> GetUsersAsync(SavedConnection c, CancellationToken ct = default);
    Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default);
    Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default);
    Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default);
}
