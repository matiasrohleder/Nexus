using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers;

/// <summary>MySQL. Containers are databases (schemas in MySQL parlance).</summary>
public class MySqlProvider : SqlProviderBase
{
    public override string Id => "mysql";
    public override string DisplayName => "MySQL";
    public override string BrandColor => "#4479A1";
    public override int DefaultPort => 3306;
    public override string DefaultUsername => "root";
    public override string DefaultDatabase => "mysql";
    public override string? ExplainPrefix => "EXPLAIN ";

    public override ProviderCapabilities Capabilities { get; } = new()
    {
        SupportsSql = true,
        SupportsSchemas = false,          // containers are databases
        SupportsServerActivity = true,
        SupportsUsers = true,
        SupportsKillSession = true,
        SupportsExplain = true,
        SupportsRowEditing = true,
        BackupTool = "mysqldump"
    };

    protected override DbConnection CreateConnection(SavedConnection c)
    {
        var csb = new MySqlConnectionStringBuilder
        {
            Server = c.Host,
            Port = (uint)c.Port,
            Database = c.Database,
            UserID = c.Username,
            Password = ConnectionStore.GetPassword(c),
            ApplicationName = "Nexus",
            ConnectionTimeout = 15,
            DefaultCommandTimeout = 0,
            AllowUserVariables = true,
            SslMode = c.SslMode.ToLowerInvariant() switch
            {
                "disable" => MySqlSslMode.Disabled,
                "require" => MySqlSslMode.Required,
                "verifyca" => MySqlSslMode.VerifyCA,
                "verifyfull" => MySqlSslMode.VerifyFull,
                _ => MySqlSslMode.Preferred
            }
        };
        return new MySqlConnection(csb.ConnectionString);
    }

    protected override void ClearPool(DbConnection connection) => MySqlConnection.ClearPool((MySqlConnection)connection);

    public override string QuoteIdent(string ident) => $"`{ident.Replace("`", "``")}`";

    public override string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null) =>
        FormatSelect("SELECT", Qualified(container, obj), columns, $"LIMIT {limit};");

    public override IReadOnlyList<string> DialectKeywords { get; } =
    [
        "AUTO_INCREMENT", "ENGINE", "INNODB", "UNSIGNED", "TINYINT", "MEDIUMTEXT", "LONGTEXT", "DATETIME",
        "ENUM", "ON DUPLICATE KEY UPDATE", "INSERT IGNORE", "REPLACE INTO", "STRAIGHT_JOIN", "SHOW TABLES",
        "SHOW DATABASES", "SHOW CREATE TABLE", "DESCRIBE", "USE", "REGEXP", "RLIKE", "LOCK TABLES", "UNLOCK TABLES"
    ];

    public override IReadOnlyList<string> DialectFunctions { get; } =
    [
        "NOW", "CURDATE", "CURTIME", "DATE_FORMAT", "DATE_ADD", "DATE_SUB", "DATEDIFF", "STR_TO_DATE",
        "UNIX_TIMESTAMP", "FROM_UNIXTIME", "IFNULL", "IF", "CONCAT_WS", "GROUP_CONCAT", "JSON_EXTRACT",
        "JSON_OBJECT", "JSON_ARRAYAGG", "LAST_INSERT_ID", "UUID", "FIND_IN_SET", "ROW_NUMBER", "RANK"
    ];

    /// <summary>MySQL containers are databases; DROP DATABASE removes everything inside.</summary>
    public override string? BuildDropContainerSql(string container, bool cascade) =>
        $"DROP DATABASE {QuoteIdent(container)}";

    // ---- introspection ---------------------------------------------------------------

    public override Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default) =>
        ReadStringsAsync(c, @"SELECT schema_name FROM information_schema.schemata
            WHERE schema_name NOT IN ('mysql','information_schema','performance_schema','sys')
            ORDER BY schema_name", ct);

    public override Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT table_name, table_type FROM information_schema.tables
                WHERE table_schema = {Lit(container)} ORDER BY table_name",
            r => new DbObjectInfo(r.GetString(0),
                (GetStringOrNull(r, 1) ?? "").Contains("VIEW") ? DbObjectKind.View : DbObjectKind.Table),
            ct);

    public override Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT column_name, column_type, is_nullable, column_key
                FROM information_schema.columns
                WHERE table_schema = {Lit(container)} AND table_name = {Lit(obj)}
                ORDER BY ordinal_position",
            r => new ColumnInfo(r.GetString(0), GetStringOrNull(r, 1) ?? "", GetStringOrNull(r, 2) == "YES",
                GetStringOrNull(r, 3) == "PRI"),
            ct);

    // ---- profiler / admin ------------------------------------------------------------

    public override Task<List<QueryLogEntry>> GetServerActivityAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT id, user, db, info, time FROM information_schema.processlist
                WHERE info IS NOT NULL AND id <> CONNECTION_ID()",
            r => new QueryLogEntry
            {
                Source = QuerySource.ServerActivity,
                Connection = $"{c.Name} ({GetStringOrNull(r, 1)})",
                Database = GetStringOrNull(r, 2) ?? "",
                Sql = GetStringOrNull(r, 3) ?? "",
                DurationMs = (GetDoubleOrNull(r, 4) ?? 0) * 1000,
                RowCount = -1,
                Success = true,
                DedupKey = $"{r.GetValue(0)}|{GetStringOrNull(r, 3)?.GetHashCode()}"
            }, ct);

    public override Task<List<SessionInfo>> GetSessionsAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT id, user, db, state, info, time * 1000
                FROM information_schema.processlist WHERE id <> CONNECTION_ID()",
            r => new SessionInfo(r.GetValue(0).ToString() ?? "", GetStringOrNull(r, 1), GetStringOrNull(r, 2),
                GetStringOrNull(r, 3), GetStringOrNull(r, 4), GetDoubleOrNull(r, 5)),
            ct);

    public override async Task KillSessionAsync(SavedConnection c, string sessionId, CancellationToken ct = default)
    {
        await ReadStringsAsync(c, $"KILL {long.Parse(sessionId)}", ct);
    }

    public override Task<List<UserInfo>> GetUsersAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT user, host, Super_priv FROM mysql.user ORDER BY user",
            r => new UserInfo(r.GetString(0), GetStringOrNull(r, 2) == "Y", $"host: {GetStringOrNull(r, 1)}"),
            ct);

    public override Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT table_name, COALESCE(table_rows, 0),
                CONCAT(ROUND((data_length + index_length) / 1024 / 1024, 1), ' MB')
            FROM information_schema.tables
            WHERE table_schema = {Lit(container)} AND table_type = 'BASE TABLE'
            ORDER BY (data_length + index_length) DESC",
            r => new TableStats(r.GetString(0), GetLongOrZero(r, 1), GetStringOrNull(r, 2)),
            ct);

    public override Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, "SHOW VARIABLES",
            r => new ServerVariable(r.GetString(0), GetStringOrNull(r, 1) ?? ""), ct);

    public override async Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        // SHOW CREATE TABLE returns (name, ddl); column 2 differs for views, so scan by count.
        var rows = await QueryAsync(c, $"SHOW CREATE TABLE {QuoteIdent(container)}.{QuoteIdent(obj)}",
            r => r.FieldCount >= 2 ? GetStringOrNull(r, 1) ?? "" : "", ct);
        return rows.Count > 0 ? rows[0] : $"-- No DDL available for {container}.{obj}";
    }
}

/// <summary>MariaDB — protocol-compatible with MySQL; same driver, different identity/branding.</summary>
public sealed class MariaDbProvider : MySqlProvider
{
    public override string Id => "mariadb";
    public override string DisplayName => "MariaDB";
    public override string BrandColor => "#003545";
}
