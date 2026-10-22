using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Nexus.Models;

namespace Nexus.Providers;

/// <summary>SQLite — file-based, single implicit container ("main").</summary>
public sealed class SqliteProvider : SqlProviderBase
{
    public override string Id => "sqlite";
    public override string DisplayName => "SQLite";
    public override string BrandColor => "#003B57";
    public override int DefaultPort => 0;
    public override string DefaultUsername => "";
    public override string DefaultDatabase => "main";
    public override string? ExplainPrefix => "EXPLAIN QUERY PLAN ";

    public override ProviderCapabilities Capabilities { get; } = new()
    {
        SupportsSql = true,
        SupportsSchemas = false,
        SupportsServerActivity = false,
        SupportsUsers = false,
        SupportsKillSession = false,
        SupportsExplain = true,
        SupportsRowEditing = true,
        IsFileBased = true,
        BackupTool = "sqlite3"
    };

    protected override DbConnection CreateConnection(SavedConnection c) =>
        new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = c.FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ConnectionString);

    protected override void ClearPool(DbConnection connection) => SqliteConnection.ClearPool((SqliteConnection)connection);

    public override string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";

    public override string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null) =>
        $"SELECT {ColumnList(columns)}\nFROM {QuoteIdent(obj)}\nLIMIT {limit};";

    public override async Task<(bool ok, string message)> TestConnectionAsync(SavedConnection c, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(c.FilePath))
            return (false, "Choose a database file first.");
        try
        {
            await using var conn = CreateConnection(c);
            await conn.OpenAsync(ct);
            return (true, $"Connected — SQLite {conn.ServerVersion}");
        }
        catch (System.Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---- introspection ---------------------------------------------------------------

    public override Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default) =>
        Task.FromResult(new List<string> { "main" });

    public override Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT name, type FROM sqlite_master
                WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name",
            r => new DbObjectInfo(r.GetString(0),
                GetStringOrNull(r, 1) == "view" ? DbObjectKind.View : DbObjectKind.Table),
            ct);

    public override Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default) =>
        QueryAsync(c, $"PRAGMA table_info({QuoteIdent(obj)})",
            // cid | name | type | notnull | dflt_value | pk
            r => new ColumnInfo(r.GetString(1), GetStringOrNull(r, 2) ?? "", GetLongOrZero(r, 3) == 0,
                GetLongOrZero(r, 5) > 0),
            ct);

    // ---- admin -----------------------------------------------------------------------

    public override async Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default)
    {
        var stats = new List<TableStats>();
        var tables = await GetObjectsAsync(c, container, ct);
        foreach (var t in tables)
        {
            if (t.Kind != DbObjectKind.Table) continue;
            var rows = await QueryAsync(c, $"SELECT COUNT(*) FROM {QuoteIdent(t.Name)}",
                r => GetLongOrZero(r, 0), ct);
            stats.Add(new TableStats(t.Name, rows.Count > 0 ? rows[0] : 0, null));
        }
        return stats;
    }

    public override async Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default)
    {
        var vars = new List<ServerVariable>();
        foreach (var pragma in new[] { "journal_mode", "page_size", "cache_size", "synchronous", "foreign_keys", "user_version", "encoding" })
        {
            var v = await QueryAsync(c, $"PRAGMA {pragma}", r => r.GetValue(0).ToString() ?? "", ct);
            if (v.Count > 0) vars.Add(new ServerVariable(pragma, v[0]));
        }
        return vars;
    }

    public override async Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        var rows = await QueryAsync(c, $"SELECT sql FROM sqlite_master WHERE name = {Lit(obj)}",
            r => GetStringOrNull(r, 0) ?? "", ct);
        return rows.Count > 0 && rows[0].Length > 0 ? rows[0] + ";" : $"-- No DDL available for {obj}";
    }
}
