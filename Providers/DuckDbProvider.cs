using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Nexus.Models;

namespace Nexus.Providers;

/// <summary>DuckDB — file-based analytical engine.</summary>
public sealed class DuckDbProvider : SqlProviderBase
{
    public override string Id => "duckdb";
    public override string DisplayName => "DuckDB";
    public override string BrandColor => "#FFF000";
    public override int DefaultPort => 0;
    public override string DefaultUsername => "";
    public override string DefaultDatabase => "main";
    public override string? ExplainPrefix => "EXPLAIN ";

    public override ProviderCapabilities Capabilities { get; } = new()
    {
        SupportsSql = true,
        SupportsSchemas = false,
        SupportsServerActivity = false,
        SupportsUsers = false,
        SupportsKillSession = false,
        SupportsExplain = true,
        SupportsRowEditing = true,
        IsFileBased = true
    };

    protected override DbConnection CreateConnection(SavedConnection c) =>
        new DuckDBConnection($"Data Source={c.FilePath}");

    public override string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";

    public override string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null) =>
        FormatSelect("SELECT", Qualified(container, obj), columns, $"LIMIT {limit};");

    public override IReadOnlyList<string> DialectKeywords { get; } =
    [
        "ILIKE", "QUALIFY", "SAMPLE", "PIVOT", "UNPIVOT", "EXCLUDE", "REPLACE", "COLUMNS", "ASOF JOIN",
        "POSITIONAL JOIN", "DESCRIBE", "SUMMARIZE", "COPY", "EXPORT DATABASE", "INSTALL", "LOAD", "STRUCT", "LIST", "MAP"
    ];

    public override IReadOnlyList<string> DialectFunctions { get; } =
    [
        "READ_CSV", "READ_CSV_AUTO", "READ_PARQUET", "READ_JSON", "NOW", "DATE_TRUNC", "STRFTIME", "STRPTIME",
        "LIST_AGG", "STRING_AGG", "ARRAY_AGG", "UNNEST", "REGEXP_MATCHES", "EPOCH", "GENERATE_SERIES", "RANGE"
    ];

    public override async Task<(bool ok, string message)> TestConnectionAsync(SavedConnection c, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(c.FilePath))
            return (false, "Choose a database file first.");
        try
        {
            await using var conn = CreateConnection(c);
            await conn.OpenAsync(ct);
            var v = await QueryAsync(c, "SELECT version()", r => GetStringOrNull(r, 0) ?? "?", ct);
            return (true, $"Connected — DuckDB {(v.Count > 0 ? v[0] : "")}");
        }
        catch (System.Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---- introspection ---------------------------------------------------------------

    public override Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default) =>
        ReadStringsAsync(c, @"SELECT schema_name FROM information_schema.schemata
            WHERE catalog_name = current_database()
              AND schema_name NOT IN ('information_schema','pg_catalog')
            ORDER BY (schema_name <> 'main'), schema_name", ct);

    public override Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT table_name, table_type FROM information_schema.tables
                WHERE table_schema = {Lit(container)} ORDER BY table_name",
            r => new DbObjectInfo(r.GetString(0),
                (GetStringOrNull(r, 1) ?? "").Contains("VIEW") ? DbObjectKind.View : DbObjectKind.Table),
            ct);

    public override Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT c.column_name, c.data_type, c.is_nullable,
                COALESCE(k.column_name IS NOT NULL, false)
            FROM information_schema.columns c
            LEFT JOIN information_schema.key_column_usage k
              ON k.table_schema = c.table_schema AND k.table_name = c.table_name
             AND k.column_name = c.column_name
            WHERE c.table_schema = {Lit(container)} AND c.table_name = {Lit(obj)}
            ORDER BY c.ordinal_position",
            r => new ColumnInfo(r.GetString(0), GetStringOrNull(r, 1) ?? "", GetStringOrNull(r, 2) == "YES",
                !r.IsDBNull(3) && r.GetBoolean(3)),
            ct);

    // ---- admin -----------------------------------------------------------------------

    public override Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT table_name, estimated_size FROM duckdb_tables()
                WHERE schema_name = {Lit(container)} ORDER BY estimated_size DESC",
            r => new TableStats(r.GetString(0), GetLongOrZero(r, 1), null), ct);

    public override Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, "SELECT name, value FROM duckdb_settings() ORDER BY name",
            r => new ServerVariable(r.GetString(0), GetStringOrNull(r, 1) ?? ""), ct);

    public override async Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        var rows = await QueryAsync(c, $@"SELECT sql FROM duckdb_tables()
            WHERE schema_name = {Lit(container)} AND table_name = {Lit(obj)}",
            r => GetStringOrNull(r, 0) ?? "", ct);
        return rows.Count > 0 && rows[0].Length > 0 ? rows[0] : $"-- No DDL available for {obj}";
    }
}
