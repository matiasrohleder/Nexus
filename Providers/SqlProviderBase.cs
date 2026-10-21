using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers;

/// <summary>
/// Shared ADO.NET implementation for all SQL engines. Subclasses supply the driver
/// (<see cref="CreateConnection"/>) and dialect-specific SQL for introspection/admin.
/// Execution, result mapping, profiling and cancellation are handled here once.
/// </summary>
public abstract class SqlProviderBase : IDatabaseProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Badge { get; }
    public abstract string BrandColor { get; }
    public abstract int DefaultPort { get; }
    public abstract string DefaultUsername { get; }
    public abstract string DefaultDatabase { get; }
    public abstract ProviderCapabilities Capabilities { get; }
    public virtual string? ExplainPrefix => null;

    /// <summary>Hard cap per result set so a runaway SELECT cannot exhaust memory.</summary>
    public const int MaxRowsPerResult = 10_000;

    /// <summary>Creates an unopened driver connection for the saved profile (password decrypted here).</summary>
    protected abstract DbConnection CreateConnection(SavedConnection c);

    public abstract string QuoteIdent(string ident);
    public abstract string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null);
    public virtual string QuoteLiteral(string value) => Lit(value);

    public virtual string? BuildDropContainerSql(string container, bool cascade) => null;

    /// <summary>DROP DATABASE for engines with a server-level database list (PG/MSSQL). Null = unsupported.</summary>
    public virtual string? BuildDropDatabaseSql(string database, bool force) => null;

    /// <summary>
    /// Called before DROP DATABASE. Engines with connection pooling must clear their pools here,
    /// otherwise Nexus's own pooled connections (from browsing the tree) block the drop.
    /// </summary>
    public virtual Task PrepareDropDatabaseAsync(SavedConnection c) => Task.CompletedTask;

    /// <summary>
    /// Formats the SELECT list: explicit quoted columns (one per line, aligned) when known,
    /// otherwise "*". Shared by every dialect's BuildSelectQuery.
    /// </summary>
    protected string ColumnList(IReadOnlyList<ColumnInfo>? columns) =>
        columns is null || columns.Count == 0
            ? "*"
            : string.Join(",\n       ", columns.Select(col => QuoteIdent(col.Name)));

    // ---- core ----------------------------------------------------------------------

    public virtual async Task<(bool ok, string message)> TestConnectionAsync(SavedConnection c, CancellationToken ct = default)
    {
        try
        {
            await using var conn = CreateConnection(c);
            await conn.OpenAsync(ct);
            var version = conn.ServerVersion;
            return (true, $"Connected — {DisplayName} {version}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public virtual async Task<List<QueryResult>> ExecuteAsync(SavedConnection c, string sql, CancellationToken ct = default)
    {
        var results = new List<QueryResult>();
        var sw = Stopwatch.StartNew();
        int totalRows = 0;

        try
        {
            await using var conn = CreateConnection(c);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;

            // Cooperative cancel: propagate token *and* force-cancel the running command.
            using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { } });

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            do
            {
                if (reader.FieldCount > 0)
                {
                    var qr = new QueryResult();
                    for (int i = 0; i < reader.FieldCount; i++)
                        qr.Columns.Add(reader.GetName(i));

                    bool truncated = false;
                    while (await reader.ReadAsync(ct))
                    {
                        if (qr.Rows.Count >= MaxRowsPerResult)
                        {
                            truncated = true;
                            break;
                        }
                        var values = new object?[reader.FieldCount];
                        for (int i = 0; i < reader.FieldCount; i++)
                            values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        qr.Rows.Add(new ResultRow(values));
                    }

                    totalRows += qr.Rows.Count;
                    qr.Message = truncated
                        ? $"{qr.Rows.Count} row(s) shown — output truncated at {MaxRowsPerResult:N0}; add a LIMIT/TOP for more control"
                        : $"{qr.Rows.Count} row(s) returned";
                    results.Add(qr);
                    if (truncated) break;
                }
            }
            while (await reader.NextResultAsync(ct));

            sw.Stop();

            if (results.Count == 0)
            {
                var affected = reader.RecordsAffected;
                results.Add(new QueryResult
                {
                    RecordsAffected = affected,
                    Message = affected >= 0 ? $"{affected} row(s) affected" : "Command executed successfully"
                });
            }

            foreach (var r in results)
                r.DurationMs = sw.Elapsed.TotalMilliseconds;

            LogToProfiler(c, sql, sw.Elapsed.TotalMilliseconds, totalRows, true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var message = ct.IsCancellationRequested ? "Query cancelled" : ex.Message;
            results.Clear();
            results.Add(new QueryResult { Error = message, Message = message, DurationMs = sw.Elapsed.TotalMilliseconds });
            LogToProfiler(c, sql, sw.Elapsed.TotalMilliseconds, -1, false, message);
        }

        return results;
    }

    public virtual async Task<int> ExecuteTransactionAsync(SavedConnection c, IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        if (statements.Count == 0) return 0;

        var sw = Stopwatch.StartNew();
        await using var conn = CreateConnection(c);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var sql in statements)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.CommandTimeout = 0;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            sw.Stop();
            LogToProfiler(c, $"-- transaction ({statements.Count} statement(s))\n{string.Join("\n", statements.Take(5))}",
                sw.Elapsed.TotalMilliseconds, statements.Count, true, null);
            return statements.Count;
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { }
            sw.Stop();
            LogToProfiler(c, $"-- transaction ({statements.Count} statement(s), rolled back)",
                sw.Elapsed.TotalMilliseconds, -1, false, ex.Message);
            throw;
        }
    }

    private void LogToProfiler(SavedConnection c, string sql, double ms, int rows, bool success, string? error) =>
        QueryProfiler.Instance.Log(new QueryLogEntry
        {
            Source = QuerySource.Application,
            Connection = c.Name,
            Database = Capabilities.IsFileBased ? System.IO.Path.GetFileName(c.FilePath) : c.Database,
            Sql = sql,
            DurationMs = ms,
            RowCount = rows,
            Success = success,
            Error = error
        });

    // ---- introspection (dialect SQL supplied by subclasses) -------------------------

    /// <summary>Default: no database level (engines whose containers already are databases, or file-based).</summary>
    public virtual Task<List<string>> GetDatabasesAsync(SavedConnection c, CancellationToken ct = default)
        => Task.FromResult(new List<string>());

    public abstract Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default);
    public abstract Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default);
    public abstract Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default);

    // ---- admin (default: unsupported / empty; override where the engine has them) ---

    public virtual Task<List<QueryLogEntry>> GetServerActivityAsync(SavedConnection c, CancellationToken ct = default)
        => Task.FromResult(new List<QueryLogEntry>());
    public virtual Task<List<SessionInfo>> GetSessionsAsync(SavedConnection c, CancellationToken ct = default)
        => Task.FromResult(new List<SessionInfo>());
    public virtual Task KillSessionAsync(SavedConnection c, string sessionId, CancellationToken ct = default)
        => throw new NotSupportedException($"{DisplayName} does not support killing sessions.");
    public virtual Task<List<UserInfo>> GetUsersAsync(SavedConnection c, CancellationToken ct = default)
        => Task.FromResult(new List<UserInfo>());
    public virtual Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default)
        => Task.FromResult(new List<TableStats>());
    public virtual Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default)
        => Task.FromResult(new List<ServerVariable>());
    public virtual Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
        => Task.FromResult($"-- DDL generation not implemented for {DisplayName}");

    // ---- shared helpers for subclasses ----------------------------------------------

    protected async Task<List<string>> ReadStringsAsync(SavedConnection c, string sql, CancellationToken ct)
    {
        var list = new List<string>();
        await using var conn = CreateConnection(c);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (!reader.IsDBNull(0))
                list.Add(reader.GetValue(0).ToString() ?? "");
        return list;
    }

    /// <summary>Runs a dialect query and maps each row through <paramref name="map"/>.</summary>
    protected async Task<List<T>> QueryAsync<T>(SavedConnection c, string sql, Func<DbDataReader, T> map,
        CancellationToken ct, params (string name, object value)[] parameters)
    {
        var list = new List<T>();
        await using var conn = CreateConnection(c);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(map(reader));
        return list;
    }

    /// <summary>
    /// Escapes a value as a single-quoted SQL literal. Used for introspection queries on engines
    /// whose ADO.NET parameter syntax differs (@p vs :p vs $p); values come from the engine's own
    /// catalogs, and quote-doubling makes them safe to embed.
    /// </summary>
    protected static string Lit(string s) => "'" + s.Replace("'", "''") + "'";

    protected static string? GetStringOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
    protected static double? GetDoubleOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));
    protected static long GetLongOrZero(DbDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i));
}
