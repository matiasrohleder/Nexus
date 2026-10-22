using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers;

public sealed class PostgresProvider : SqlProviderBase
{
    public override string Id => "postgresql";
    public override string DisplayName => "PostgreSQL";
    public override string Badge => "Pg";
    public override string BrandColor => "#336791";
    public override int DefaultPort => 5432;
    public override string DefaultUsername => "postgres";
    public override string DefaultDatabase => "postgres";
    public override string? ExplainPrefix => "EXPLAIN (ANALYZE false, VERBOSE false) ";

    public override ProviderCapabilities Capabilities { get; } = new()
    {
        SupportsSql = true,
        SupportsSchemas = true,
        SupportsServerActivity = true,
        SupportsUsers = true,
        SupportsKillSession = true,
        SupportsExplain = true,
        SupportsRowEditing = true,
        BackupTool = "pg_dump"
    };

    protected override DbConnection CreateConnection(SavedConnection c)
    {
        var sslMode = Enum.TryParse<SslMode>(c.SslMode, ignoreCase: true, out var m) ? m : SslMode.Prefer;
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = c.Host,
            Port = c.Port,
            Database = c.Database,
            Username = c.Username,
            Password = ConnectionStore.GetPassword(c),
            SslMode = sslMode,
            ApplicationName = "Nexus",
            Timeout = 15,
            CommandTimeout = 0,
            IncludeErrorDetail = true
        };
        return new NpgsqlConnection(csb.ConnectionString);
    }

    protected override void ClearPool(DbConnection connection) => NpgsqlConnection.ClearPool((NpgsqlConnection)connection);

    public override string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";

    public override string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null) =>
        $"SELECT {ColumnList(columns)}\nFROM {QuoteIdent(container)}.{QuoteIdent(obj)}\nLIMIT {limit};";

    public override string? BuildDropContainerSql(string container, bool cascade) =>
        $"DROP SCHEMA {QuoteIdent(container)}{(cascade ? " CASCADE" : "")}";

    public override string? BuildDropDatabaseSql(string database, bool force) =>
        force ? $"DROP DATABASE {QuoteIdent(database)} WITH (FORCE)"
              : $"DROP DATABASE {QuoteIdent(database)}";

    public override Task PrepareDropDatabaseAsync(SavedConnection c)
    {
        // Nexus's own pooled connections (tree browsing, queries) count as "other users".
        NpgsqlConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    // ---- introspection ---------------------------------------------------------------

    public override Task<List<string>> GetDatabasesAsync(SavedConnection c, CancellationToken ct = default) =>
        ReadStringsAsync(c, @"SELECT datname FROM pg_database
            WHERE NOT datistemplate AND datallowconn
            ORDER BY (datname <> current_database()), datname", ct);

    public override Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default) =>
        ReadStringsAsync(c, @"SELECT nspname FROM pg_namespace
            WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema'
            ORDER BY (nspname <> 'public'), nspname", ct);

    public override Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT table_name, table_type FROM information_schema.tables
                WHERE table_schema = @s ORDER BY table_name",
            r => new DbObjectInfo(r.GetString(0),
                (GetStringOrNull(r, 1) ?? "").Contains("VIEW") ? DbObjectKind.View : DbObjectKind.Table),
            ct, ("s", container));

    public override Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT c.column_name, c.data_type, c.is_nullable,
                EXISTS (
                    SELECT 1 FROM information_schema.table_constraints tc
                    JOIN information_schema.key_column_usage kcu
                      ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                    WHERE tc.constraint_type = 'PRIMARY KEY'
                      AND tc.table_schema = c.table_schema AND tc.table_name = c.table_name
                      AND kcu.column_name = c.column_name
                ) AS is_pk
            FROM information_schema.columns c
            WHERE c.table_schema = @s AND c.table_name = @t
            ORDER BY c.ordinal_position",
            r => new ColumnInfo(r.GetString(0), r.GetString(1), r.GetString(2) == "YES", r.GetBoolean(3)),
            ct, ("s", container), ("t", obj));

    // ---- profiler --------------------------------------------------------------------

    public override Task<List<QueryLogEntry>> GetServerActivityAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT pid, usename, datname, query,
                EXTRACT(EPOCH FROM (now() - query_start)) * 1000 AS ms, query_start
            FROM pg_stat_activity
            WHERE query IS NOT NULL AND query <> '' AND pid <> pg_backend_pid() AND state IS NOT NULL
            ORDER BY query_start DESC NULLS LAST",
            r =>
            {
                var user = GetStringOrNull(r, 1);
                var startTicks = r.IsDBNull(5) ? 0 : r.GetDateTime(5).Ticks;
                return new QueryLogEntry
                {
                    Source = QuerySource.ServerActivity,
                    Connection = string.IsNullOrEmpty(user) ? c.Name : $"{c.Name} ({user})",
                    Database = GetStringOrNull(r, 2) ?? "",
                    Sql = GetStringOrNull(r, 3) ?? "",
                    DurationMs = GetDoubleOrNull(r, 4) ?? -1,
                    RowCount = -1,
                    Success = true,
                    DedupKey = $"{r.GetInt32(0)}|{startTicks}"
                };
            }, ct);

    // ---- admin -------------------------------------------------------------------------

    public override Task<List<SessionInfo>> GetSessionsAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT pid::text, usename, datname, state, query,
                EXTRACT(EPOCH FROM (now() - query_start)) * 1000
            FROM pg_stat_activity
            WHERE pid <> pg_backend_pid()
            ORDER BY query_start DESC NULLS LAST",
            r => new SessionInfo(r.GetString(0), GetStringOrNull(r, 1), GetStringOrNull(r, 2),
                GetStringOrNull(r, 3), GetStringOrNull(r, 4), GetDoubleOrNull(r, 5)),
            ct);

    public override async Task KillSessionAsync(SavedConnection c, string sessionId, CancellationToken ct = default)
    {
        await QueryAsync(c, "SELECT pg_terminate_backend(@p)", r => r.GetValue(0), ct, ("p", int.Parse(sessionId)));
    }

    public override Task<List<UserInfo>> GetUsersAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT rolname, rolsuper,
                concat_ws(', ',
                    CASE WHEN rolcanlogin THEN 'login' END,
                    CASE WHEN rolcreatedb THEN 'createdb' END,
                    CASE WHEN rolcreaterole THEN 'createrole' END)
            FROM pg_roles WHERE rolname NOT LIKE 'pg\_%' ORDER BY rolname",
            r => new UserInfo(r.GetString(0), r.GetBoolean(1), GetStringOrNull(r, 2)),
            ct);

    public override Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT relname, COALESCE(n_live_tup, 0),
                pg_size_pretty(pg_total_relation_size(relid))
            FROM pg_stat_user_tables WHERE schemaname = @s
            ORDER BY pg_total_relation_size(relid) DESC",
            r => new TableStats(r.GetString(0), GetLongOrZero(r, 1), GetStringOrNull(r, 2)),
            ct, ("s", container));

    public override Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, "SELECT name, setting FROM pg_settings ORDER BY name",
            r => new ServerVariable(r.GetString(0), GetStringOrNull(r, 1) ?? ""), ct);

    public override async Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        // Postgres has no built-in "show create table"; reconstruct a usable CREATE script.
        var columns = await GetColumnsAsync(c, container, obj, ct);
        if (columns.Count == 0) return $"-- No columns found for {container}.{obj}";

        var pk = columns.Where(x => x.IsPrimaryKey).Select(x => QuoteIdent(x.Name)).ToList();
        var lines = columns.Select(col =>
            $"    {QuoteIdent(col.Name)} {col.DataType}{(col.Nullable ? "" : " NOT NULL")}").ToList();
        if (pk.Count > 0)
            lines.Add($"    PRIMARY KEY ({string.Join(", ", pk)})");

        return $"CREATE TABLE {QuoteIdent(container)}.{QuoteIdent(obj)} (\n{string.Join(",\n", lines)}\n);";
    }
}
