using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Oracle.ManagedDataAccess.Client;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers;

/// <summary>Oracle Database (managed driver, EZConnect). Containers are schemas (owners).</summary>
public sealed class OracleProvider : SqlProviderBase
{
    public override string Id => "oracle";
    public override string DisplayName => "Oracle";
    public override string BrandColor => "#F80000";
    public override int DefaultPort => 1521;
    public override string DefaultUsername => "system";
    public override string DefaultDatabase => "FREEPDB1";
    public override string? ExplainPrefix => null; // EXPLAIN PLAN needs a second query on PLAN_TABLE

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
        var csb = new OracleConnectionStringBuilder
        {
            // EZConnect: host:port/service_name — "Database" holds the service name.
            DataSource = $"{c.Host}:{c.Port}/{c.Database}",
            UserID = c.Username,
            Password = ConnectionStore.GetPassword(c),
            ConnectionTimeout = 15
        };
        return new OracleConnection(csb.ConnectionString);
    }

    protected override void ClearPool(DbConnection connection) => OracleConnection.ClearPool((OracleConnection)connection);

    public override string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";

    public override string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null) =>
        FormatSelect("SELECT", Qualified(container, obj), columns, $"FETCH FIRST {limit} ROWS ONLY");

    protected override IdentifierCase UnquotedCase => IdentifierCase.Upper;

    private static readonly HashSet<string> OracleReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCESS", "AUDIT", "CLUSTER", "COMMENT", "COMPRESS", "DATE", "FILE", "IDENTIFIED", "IMMEDIATE", "INCREMENT",
        "INITIAL", "LEVEL", "LOCK", "LONG", "MAXEXTENTS", "MINUS", "MODE", "MODIFY", "NOAUDIT", "NOCOMPRESS",
        "NUMBER", "OFFLINE", "ONLINE", "PCTFREE", "PRIOR", "PUBLIC", "RAW", "RENAME", "RESOURCE", "ROWID",
        "ROWNUM", "SESSION", "SHARE", "SIZE", "START", "SYNONYM", "SYSDATE", "TRIGGER", "UID", "VALIDATE",
        "VARCHAR", "VARCHAR2"
    };

    protected override bool IsReservedWord(string word) => base.IsReservedWord(word) || OracleReserved.Contains(word);

    public override IReadOnlyList<string> DialectKeywords { get; } =
    [
        "FETCH FIRST", "ROWS ONLY", "ROWNUM", "DUAL", "CONNECT BY", "START WITH", "PRIOR", "MINUS", "MERGE INTO",
        "VARCHAR2", "NVARCHAR2", "NUMBER", "CLOB", "BLOB", "SYSDATE", "SYSTIMESTAMP", "SEQUENCE", "NEXTVAL",
        "CURRVAL", "PURGE", "BEGIN", "DECLARE", "EXCEPTION", "PACKAGE", "PROCEDURE"
    ];

    public override IReadOnlyList<string> DialectFunctions { get; } =
    [
        "NVL", "NVL2", "DECODE", "TO_CHAR", "TO_DATE", "TO_NUMBER", "TRUNC", "ADD_MONTHS", "MONTHS_BETWEEN",
        "LISTAGG", "INSTR", "SUBSTR", "REGEXP_LIKE", "REGEXP_SUBSTR", "SYS_GUID", "ROW_NUMBER", "RANK"
    ];

    /// <summary>Oracle schemas are users; dropping one requires CASCADE when it owns objects.</summary>
    public override string? BuildDropContainerSql(string container, bool cascade) =>
        $"DROP USER {QuoteIdent(container)}{(cascade ? " CASCADE" : "")}";

    // ---- introspection ---------------------------------------------------------------

    public override Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default) =>
        ReadStringsAsync(c, @"SELECT username FROM all_users
            WHERE oracle_maintained = 'N' OR username = USER
            ORDER BY CASE WHEN username = USER THEN 0 ELSE 1 END, username", ct);

    public override Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT object_name, object_type FROM all_objects
                WHERE owner = {Lit(container)} AND object_type IN ('TABLE','VIEW')
                ORDER BY object_name",
            r => new DbObjectInfo(r.GetString(0),
                GetStringOrNull(r, 1) == "VIEW" ? DbObjectKind.View : DbObjectKind.Table),
            ct);

    public override Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT c.column_name, c.data_type, c.nullable,
                CASE WHEN pk.column_name IS NOT NULL THEN 1 ELSE 0 END
            FROM all_tab_columns c
            LEFT JOIN (
                SELECT cc.column_name FROM all_constraints con
                JOIN all_cons_columns cc
                  ON con.constraint_name = cc.constraint_name AND con.owner = cc.owner
                WHERE con.constraint_type = 'P'
                  AND con.owner = {Lit(container)} AND con.table_name = {Lit(obj)}
            ) pk ON pk.column_name = c.column_name
            WHERE c.owner = {Lit(container)} AND c.table_name = {Lit(obj)}
            ORDER BY c.column_id",
            r => new ColumnInfo(r.GetString(0), GetStringOrNull(r, 1) ?? "", GetStringOrNull(r, 2) == "Y",
                GetLongOrZero(r, 3) == 1),
            ct);

    // ---- profiler / admin ------------------------------------------------------------

    public override Task<List<QueryLogEntry>> GetServerActivityAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT s.sid || ',' || s.serial#, s.username, s.machine, q.sql_text,
                s.last_call_et * 1000
            FROM v$session s
            JOIN v$sql q ON q.sql_id = s.sql_id
            WHERE s.status = 'ACTIVE' AND s.type = 'USER' AND s.audsid <> SYS_CONTEXT('USERENV','SESSIONID')",
            r => new QueryLogEntry
            {
                Source = QuerySource.ServerActivity,
                Connection = $"{c.Name} ({GetStringOrNull(r, 1)})",
                Database = GetStringOrNull(r, 2) ?? "",
                Sql = GetStringOrNull(r, 3) ?? "",
                DurationMs = GetDoubleOrNull(r, 4) ?? -1,
                RowCount = -1,
                Success = true,
                DedupKey = $"{GetStringOrNull(r, 0)}|{GetStringOrNull(r, 3)?.GetHashCode()}"
            }, ct);

    public override Task<List<SessionInfo>> GetSessionsAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT s.sid || ',' || s.serial#, s.username, s.machine, s.status,
                (SELECT q.sql_text FROM v$sql q WHERE q.sql_id = s.sql_id AND ROWNUM = 1),
                s.last_call_et * 1000
            FROM v$session s WHERE s.type = 'USER'",
            r => new SessionInfo(GetStringOrNull(r, 0) ?? "", GetStringOrNull(r, 1), GetStringOrNull(r, 2),
                GetStringOrNull(r, 3), GetStringOrNull(r, 4), GetDoubleOrNull(r, 5)),
            ct);

    public override async Task KillSessionAsync(SavedConnection c, string sessionId, CancellationToken ct = default)
    {
        // sessionId format: "sid,serial#" — validate to digits and comma before embedding.
        foreach (var ch in sessionId)
            if (!char.IsDigit(ch) && ch != ',')
                throw new System.ArgumentException("Invalid Oracle session id.");
        await ReadStringsAsync(c, $"ALTER SYSTEM KILL SESSION '{sessionId}' IMMEDIATE", ct);
    }

    public override Task<List<UserInfo>> GetUsersAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, @"SELECT username, CASE WHEN username IN ('SYS','SYSTEM') THEN 1 ELSE 0 END,
                TO_CHAR(created, 'YYYY-MM-DD')
            FROM all_users WHERE oracle_maintained = 'N' OR username IN ('SYS','SYSTEM')
            ORDER BY username",
            r => new UserInfo(r.GetString(0), GetLongOrZero(r, 1) == 1, "created " + GetStringOrNull(r, 2)),
            ct);

    public override Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default) =>
        QueryAsync(c, $@"SELECT table_name, NVL(num_rows, 0), NULL FROM all_tables
                WHERE owner = {Lit(container)} ORDER BY NVL(num_rows, 0) DESC",
            r => new TableStats(r.GetString(0), GetLongOrZero(r, 1), null), ct);

    public override Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default) =>
        QueryAsync(c, "SELECT name, value FROM v$parameter ORDER BY name",
            r => new ServerVariable(r.GetString(0), GetStringOrNull(r, 1) ?? ""), ct);

    public override async Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        var rows = await QueryAsync(c,
            $"SELECT DBMS_METADATA.GET_DDL('TABLE', {Lit(obj)}, {Lit(container)}) FROM dual",
            r => GetStringOrNull(r, 0) ?? "", ct);
        return rows.Count > 0 && rows[0].Length > 0 ? rows[0] : $"-- No DDL available for {container}.{obj}";
    }
}
