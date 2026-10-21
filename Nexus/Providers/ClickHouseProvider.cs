using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Client.ADO;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers;

/// <summary>ClickHouse (HTTP interface, default port 8123). Containers are databases.</summary>
public sealed class ClickHouseProvider : SqlProviderBase
{
    public override string Id => "clickhouse";
    public override string DisplayName => "ClickHouse";
    public override string Glyph => "⚡";
    public override int DefaultPort => 8123;
    public override string DefaultUsername => "default";
    public override string DefaultDatabase => "default";
    public override string? ExplainPrefix => "EXPLAIN ";

    public override ProviderCapabilities Capabilities { get; } = new()
    {
        SupportsSql = true,
        SupportsSchemas = false,
        SupportsServerActivity = true,
        SupportsUsers = true,
        SupportsKillSession = true,
        SupportsExplain = true,
        SupportsRowEditing = false      // OLAP: no classic UPDATE/DELETE by PK
    };

    protected override DbConnection CreateConnection(SavedConnection c)
    {
        var protocol = c.SslMode.Equals("Disable", System.StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        // Port 8123 is plain HTTP in default server config; honor SslMode=Disable for local dev.
        if (c.Port == 8123) protocol = "http";
        var cs = $"Host={c.Host};Port={c.Port};Protocol={protocol};Username={c.Username};" +
                 $"Password={ConnectionStore.GetPassword(c)};Database={c.Database}";
        return new ClickHouseConnection(cs);
    }

    public override string QuoteIdent(string ident) => $"`{ident.Replace("`", "\\`")}`";

    public override string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null) =>
        $"SELECT {ColumnList(columns)}\nFROM {QuoteIdent(container)}.{QuoteIdent(obj)}\nLIMIT {limit};";

    public override string? BuildDropContainerSql(string container, bool cascade) =>
        $"DROP DATABASE {QuoteIdent(container)}";

    // ---- introspection ---------------------------------------------------------------

    public override Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default) =>
        ReadStringsAsync(c, @"SELECT name FROM system.databases
            WHERE name NOT IN ('system','INFORMATION_SCHEMA','information_schema') ORDER BY name", ct);

    public override Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT name, engine FROM system.tables
                WHERE database = {Lit(container)} ORDER BY name",
            r => new DbObjectInfo(r.GetString(0),
                (GetStringOrNull(r, 1) ?? "").Contains("View") ? DbObjectKind.View : DbObjectKind.Table),
            ct);

    public override Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT name, type, is_in_primary_key FROM system.columns
                WHERE database = {Lit(container)} AND table = {Lit(obj)} ORDER BY position",
            r => new ColumnInfo(r.GetString(0), GetStringOrNull(r, 1) ?? "",
                (GetStringOrNull(r, 1) ?? "").StartsWith("Nullable"),
                GetLongOrZero(r, 2) == 1),
            ct);

    // ---- profiler / admin ------------------------------------------------------------

    public override Task<List<QueryLogEntry>> GetServerActivityAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT query_id, user, current_database, query, elapsed * 1000
            FROM system.processes WHERE query NOT LIKE '%system.processes%'",
            r => new QueryLogEntry
            {
                Source = QuerySource.ServerActivity,
                Connection = $"{c.Name} ({GetStringOrNull(r, 1)})",
                Database = GetStringOrNull(r, 2) ?? "",
                Sql = GetStringOrNull(r, 3) ?? "",
                DurationMs = GetDoubleOrNull(r, 4) ?? -1,
                RowCount = -1,
                Success = true,
                DedupKey = GetStringOrNull(r, 0) ?? ""
            }, ct);

    public override Task<List<SessionInfo>> GetSessionsAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT query_id, user, current_database, 'running', query, elapsed * 1000
            FROM system.processes",
            r => new SessionInfo(GetStringOrNull(r, 0) ?? "", GetStringOrNull(r, 1), GetStringOrNull(r, 2),
                GetStringOrNull(r, 3), GetStringOrNull(r, 4), GetDoubleOrNull(r, 5)),
            ct);

    public override async Task KillSessionAsync(SavedConnection c, string sessionId, CancellationToken ct = default)
    {
        await ReadStringsAsync(c, $"KILL QUERY WHERE query_id = {Lit(sessionId)}", ct);
    }

    public override Task<List<UserInfo>> GetUsersAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, "SELECT name, 0, storage FROM system.users ORDER BY name",
            r => new UserInfo(r.GetString(0), false, GetStringOrNull(r, 2)), ct);

    public override Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT table, SUM(rows), formatReadableSize(SUM(bytes_on_disk))
            FROM system.parts WHERE database = {Lit(container)} AND active
            GROUP BY table ORDER BY SUM(bytes_on_disk) DESC",
            r => new TableStats(r.GetString(0), GetLongOrZero(r, 1), GetStringOrNull(r, 2)),
            ct);

    public override Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, "SELECT name, value FROM system.settings ORDER BY name",
            r => new ServerVariable(r.GetString(0), GetStringOrNull(r, 1) ?? ""), ct);

    public override async Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        var rows = await QueryAsync(c, $"SHOW CREATE TABLE {QuoteIdent(container)}.{QuoteIdent(obj)}",
            r => GetStringOrNull(r, 0) ?? "", ct);
        return rows.Count > 0 ? rows[0] : $"-- No DDL available for {container}.{obj}";
    }
}
