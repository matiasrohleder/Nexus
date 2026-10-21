using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers;

/// <summary>Microsoft SQL Server. Containers are schemas within the connected database.</summary>
public sealed class SqlServerProvider : SqlProviderBase
{
    public override string Id => "sqlserver";
    public override string DisplayName => "SQL Server";
    public override string Badge => "MS";
    public override string BrandColor => "#B91C1C";
    public override int DefaultPort => 1433;
    public override string DefaultUsername => "sa";
    public override string DefaultDatabase => "master";
    public override string? ExplainPrefix => null; // SHOWPLAN needs session toggles; not a simple prefix

    public override ProviderCapabilities Capabilities { get; } = new()
    {
        SupportsSql = true,
        SupportsSchemas = true,
        SupportsServerActivity = true,
        SupportsUsers = true,
        SupportsKillSession = true,
        SupportsExplain = false,
        SupportsRowEditing = true
    };

    protected override DbConnection CreateConnection(SavedConnection c)
    {
        var csb = new SqlConnectionStringBuilder
        {
            DataSource = $"{c.Host},{c.Port}",
            InitialCatalog = c.Database,
            UserID = c.Username,
            Password = ConnectionStore.GetPassword(c),
            ApplicationName = "Nexus",
            ConnectTimeout = 15,
            // Dev-friendly TLS defaults; "Disable" turns encryption off entirely.
            Encrypt = c.SslMode.Equals("Disable", System.StringComparison.OrdinalIgnoreCase)
                ? SqlConnectionEncryptOption.Optional
                : SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true
        };
        return new SqlConnection(csb.ConnectionString);
    }

    public override string QuoteIdent(string ident) => $"[{ident.Replace("]", "]]")}]";

    public override string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null) =>
        $"SELECT TOP {limit} {ColumnList(columns)}\nFROM {QuoteIdent(container)}.{QuoteIdent(obj)};";

    public override string? BuildDropContainerSql(string container, bool cascade) =>
        $"DROP SCHEMA {QuoteIdent(container)}"; // MSSQL has no CASCADE; objects must go first

    public override string? BuildDropDatabaseSql(string database, bool force) =>
        force ? $"ALTER DATABASE {QuoteIdent(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\nDROP DATABASE {QuoteIdent(database)};"
              : $"DROP DATABASE {QuoteIdent(database)}";

    public override Task PrepareDropDatabaseAsync(SavedConnection c)
    {
        SqlConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    // ---- introspection ---------------------------------------------------------------

    public override Task<List<string>> GetDatabasesAsync(SavedConnection c, CancellationToken ct = default) =>
        ReadStringsAsync(c, @"SELECT name FROM sys.databases
            WHERE state = 0
            ORDER BY CASE WHEN name = DB_NAME() THEN 0 WHEN database_id <= 4 THEN 2 ELSE 1 END, name", ct);

    public override Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default) =>
        ReadStringsAsync(c, @"SELECT name FROM sys.schemas
            WHERE schema_id < 16384 AND name NOT IN ('sys','INFORMATION_SCHEMA','guest')
            ORDER BY CASE WHEN name = 'dbo' THEN 0 ELSE 1 END, name", ct);

    public override Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT table_name, table_type FROM information_schema.tables
                WHERE table_schema = {Lit(container)} ORDER BY table_name",
            r => new DbObjectInfo(r.GetString(0),
                (GetStringOrNull(r, 1) ?? "").Contains("VIEW") ? DbObjectKind.View : DbObjectKind.Table),
            ct);

    public override Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT c.column_name, c.data_type, c.is_nullable,
                CASE WHEN k.column_name IS NOT NULL THEN 1 ELSE 0 END
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT kcu.column_name, kcu.table_schema, kcu.table_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                WHERE tc.constraint_type = 'PRIMARY KEY'
            ) k ON k.table_schema = c.table_schema AND k.table_name = c.table_name AND k.column_name = c.column_name
            WHERE c.table_schema = {Lit(container)} AND c.table_name = {Lit(obj)}
            ORDER BY c.ordinal_position",
            r => new ColumnInfo(r.GetString(0), GetStringOrNull(r, 1) ?? "", GetStringOrNull(r, 2) == "YES",
                GetLongOrZero(r, 3) == 1),
            ct);

    // ---- profiler / admin ------------------------------------------------------------

    public override Task<List<QueryLogEntry>> GetServerActivityAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT r.session_id, s.login_name, DB_NAME(r.database_id), t.text,
                r.total_elapsed_time, CONVERT(varchar(30), r.start_time, 126)
            FROM sys.dm_exec_requests r
            JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.session_id <> @@SPID AND t.text IS NOT NULL",
            r => new QueryLogEntry
            {
                Source = QuerySource.ServerActivity,
                Connection = $"{c.Name} ({GetStringOrNull(r, 1)})",
                Database = GetStringOrNull(r, 2) ?? "",
                Sql = GetStringOrNull(r, 3) ?? "",
                DurationMs = GetDoubleOrNull(r, 4) ?? -1,
                RowCount = -1,
                Success = true,
                DedupKey = $"{r.GetValue(0)}|{GetStringOrNull(r, 5)}"
            }, ct);

    public override Task<List<SessionInfo>> GetSessionsAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT s.session_id, s.login_name, DB_NAME(s.database_id), s.status,
                (SELECT TOP 1 t.text FROM sys.dm_exec_requests r
                  CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
                  WHERE r.session_id = s.session_id),
                DATEDIFF(ms, s.last_request_start_time, GETDATE())
            FROM sys.dm_exec_sessions s
            WHERE s.is_user_process = 1 AND s.session_id <> @@SPID",
            r => new SessionInfo(r.GetValue(0).ToString() ?? "", GetStringOrNull(r, 1), GetStringOrNull(r, 2),
                GetStringOrNull(r, 3), GetStringOrNull(r, 4), GetDoubleOrNull(r, 5)),
            ct);

    public override async Task KillSessionAsync(SavedConnection c, string sessionId, CancellationToken ct = default)
    {
        await ReadStringsAsync(c, $"KILL {int.Parse(sessionId)}", ct);
    }

    public override Task<List<UserInfo>> GetUsersAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT name, IS_SRVROLEMEMBER('sysadmin', name), type_desc
            FROM sys.server_principals WHERE type IN ('S','U') AND name NOT LIKE '##%' ORDER BY name",
            r => new UserInfo(r.GetString(0), GetLongOrZero(r, 1) == 1, GetStringOrNull(r, 2)),
            ct);

    public override Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT t.name, SUM(p.rows),
                CONCAT(CAST(SUM(a.total_pages) * 8 / 1024.0 AS decimal(18,1)), ' MB')
            FROM sys.tables t
            JOIN sys.schemas sc ON sc.schema_id = t.schema_id
            JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0,1)
            JOIN sys.allocation_units a ON a.container_id = p.partition_id
            WHERE sc.name = {Lit(container)}
            GROUP BY t.name ORDER BY SUM(a.total_pages) DESC",
            r => new TableStats(r.GetString(0), GetLongOrZero(r, 1), GetStringOrNull(r, 2)),
            ct);

    public override Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, "SELECT name, CAST(value_in_use AS varchar(200)) FROM sys.configurations ORDER BY name",
            r => new ServerVariable(r.GetString(0), GetStringOrNull(r, 1) ?? ""), ct);

    public override async Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        var columns = await GetColumnsAsync(c, container, obj, ct);
        if (columns.Count == 0) return $"-- No columns found for {container}.{obj}";

        var pk = new List<string>();
        var lines = new List<string>();
        foreach (var col in columns)
        {
            lines.Add($"    {QuoteIdent(col.Name)} {col.DataType}{(col.Nullable ? " NULL" : " NOT NULL")}");
            if (col.IsPrimaryKey) pk.Add(QuoteIdent(col.Name));
        }
        if (pk.Count > 0)
            lines.Add($"    PRIMARY KEY ({string.Join(", ", pk)})");

        return $"CREATE TABLE {QuoteIdent(container)}.{QuoteIdent(obj)} (\n{string.Join(",\n", lines)}\n);";
    }
}
