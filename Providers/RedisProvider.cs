using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers;

/// <summary>
/// Redis. Containers are the non-empty numbered databases (db0…db15). Objects are keys
/// (sampled via SCAN, never KEYS *). The editor accepts raw Redis commands (GET, SET, HGETALL,
/// SCAN, …) plus the Nexus pseudo-command INSPECT &lt;key&gt; which shows type/TTL/value.
/// </summary>
public sealed class RedisProvider : IDatabaseProvider
{
    public string Id => "redis";
    public string DisplayName => "Redis";
    public string Badge => "Rd";
    public string BrandColor => "#DC2626";
    public int DefaultPort => 6379;
    public string DefaultUsername => "";
    public string DefaultDatabase => "0";
    public string? ExplainPrefix => null;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        SupportsSql = false,
        SupportsSchemas = false,
        SupportsServerActivity = true,   // SLOWLOG
        SupportsUsers = true,            // ACL LIST
        SupportsKillSession = true,      // CLIENT KILL
        SupportsExplain = false,
        SupportsRowEditing = false
    };

    private const int ScanSampleLimit = 500;

    private async Task<ConnectionMultiplexer> ConnectAsync(SavedConnection c)
    {
        var options = new ConfigurationOptions
        {
            EndPoints = { { c.Host, c.Port } },
            ConnectTimeout = 15000,
            AllowAdmin = true,
            AbortOnConnectFail = false
        };
        var password = ConnectionStore.GetPassword(c);
        if (!string.IsNullOrEmpty(password)) options.Password = password;
        if (!string.IsNullOrEmpty(c.Username)) options.User = c.Username;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    /// <summary>Accepts both "3" and "db3" (the explorer/picker use the db-prefixed form).</summary>
    private static int DbIndex(SavedConnection c) =>
        int.TryParse(c.Database.TrimStart('d', 'b'), out var i) ? i : 0;

    public string QuoteIdent(string ident) => ident;
    public string QuoteLiteral(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    public string? BuildDropContainerSql(string container, bool cascade) => null; // use FLUSHDB manually
    public string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null) =>
        $"INSPECT {obj}";

    public async Task<(bool ok, string message)> TestConnectionAsync(SavedConnection c, CancellationToken ct = default)
    {
        try
        {
            using var mux = await ConnectAsync(c);
            var server = mux.GetServer(mux.GetEndPoints()[0]);
            var info = await server.InfoAsync("server");
            var version = info.SelectMany(g => g).FirstOrDefault(kv => kv.Key == "redis_version").Value ?? "?";
            return (true, $"Connected — Redis {version}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---- command execution -----------------------------------------------------------------

    public async Task<List<QueryResult>> ExecuteAsync(SavedConnection c, string commandText, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new QueryResult();
        bool success = true;
        string? error = null;

        try
        {
            using var mux = await ConnectAsync(c);
            var db = mux.GetDatabase(DbIndex(c));

            var tokens = Tokenize(commandText.Trim());
            if (tokens.Count == 0) throw new ArgumentException("Empty command.");

            if (tokens[0].Equals("INSPECT", StringComparison.OrdinalIgnoreCase))
            {
                if (tokens.Count < 2) throw new ArgumentException("INSPECT <key>");
                await InspectKeyAsync(db, tokens[1], result);
            }
            else
            {
                var raw = await db.ExecuteAsync(tokens[0], tokens.Skip(1).ToArray());
                FillFromRedisResult(result, raw);
            }

            result.Message = result.HasResultSet ? $"{result.Rows.Count} row(s)" : "OK";
        }
        catch (Exception ex)
        {
            success = false;
            error = ex.Message;
            result = new QueryResult { Error = ex.Message, Message = ex.Message };
        }

        sw.Stop();
        result.DurationMs = sw.Elapsed.TotalMilliseconds;

        QueryProfiler.Instance.Log(new QueryLogEntry
        {
            Source = QuerySource.Application,
            Connection = c.Name,
            Database = $"db{DbIndex(c)}",
            Sql = commandText,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            RowCount = result.Rows.Count,
            Success = success,
            Error = error
        });

        return new List<QueryResult> { result };
    }

    public Task<int> ExecuteTransactionAsync(SavedConnection c, IReadOnlyList<string> statements, CancellationToken ct = default)
        => throw new NotSupportedException("Redis provider does not support SQL-style transactions.");

    /// <summary>Splits a command line into tokens, honoring double-quoted strings.</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var ch in text)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(ch);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static async Task InspectKeyAsync(IDatabase db, string key, QueryResult result)
    {
        var type = await db.KeyTypeAsync(key);
        var ttl = await db.KeyTimeToLiveAsync(key);

        result.Columns.Add("field");
        result.Columns.Add("value");
        result.Rows.Add(new ResultRow(new object?[] { "(type)", type.ToString() }));
        result.Rows.Add(new ResultRow(new object?[] { "(ttl)", ttl?.ToString() ?? "∞" }));

        switch (type)
        {
            case RedisType.String:
                result.Rows.Add(new ResultRow(new object?[] { "(value)", (string?)await db.StringGetAsync(key) }));
                break;
            case RedisType.Hash:
                foreach (var entry in await db.HashGetAllAsync(key))
                    result.Rows.Add(new ResultRow(new object?[] { (string?)entry.Name, (string?)entry.Value }));
                break;
            case RedisType.List:
                var list = await db.ListRangeAsync(key, 0, 199);
                for (int i = 0; i < list.Length; i++)
                    result.Rows.Add(new ResultRow(new object?[] { $"[{i}]", (string?)list[i] }));
                break;
            case RedisType.Set:
                foreach (var member in await db.SetMembersAsync(key))
                    result.Rows.Add(new ResultRow(new object?[] { "(member)", (string?)member }));
                break;
            case RedisType.SortedSet:
                foreach (var entry in await db.SortedSetRangeByRankWithScoresAsync(key, 0, 199))
                    result.Rows.Add(new ResultRow(new object?[] { entry.Score.ToString(), (string?)entry.Element }));
                break;
            default:
                result.Rows.Add(new ResultRow(new object?[] { "(value)", "(unsupported type)" }));
                break;
        }
    }

    private static void FillFromRedisResult(QueryResult result, RedisResult raw)
    {
        result.Columns.Add("value");
        if (raw.Resp2Type == ResultType.Array && !raw.IsNull)
        {
            foreach (var item in (RedisResult[])raw!)
                result.Rows.Add(new ResultRow(new object?[] { item.ToString() }));
        }
        else
        {
            result.Rows.Add(new ResultRow(new object?[] { raw.ToString() }));
        }
    }

    // ---- introspection -----------------------------------------------------------------------

    /// <summary>Containers already are the numbered keyspaces — no extra database level.</summary>
    public Task<List<string>> GetDatabasesAsync(SavedConnection c, CancellationToken ct = default)
        => Task.FromResult(new List<string>());

    public async Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default)
    {
        using var mux = await ConnectAsync(c);
        var server = mux.GetServer(mux.GetEndPoints()[0]);
        var info = await server.InfoAsync("keyspace");
        var dbs = info.SelectMany(g => g).Select(kv => kv.Key).Where(k => k.StartsWith("db")).ToList();
        return dbs.Count > 0 ? dbs : new List<string> { "db0" };
    }

    public async Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default)
    {
        using var mux = await ConnectAsync(c);
        var server = mux.GetServer(mux.GetEndPoints()[0]);
        var dbIndex = int.TryParse(container.TrimStart('d', 'b'), out var i) ? i : 0;

        var keys = new List<DbObjectInfo>();
        await foreach (var key in server.KeysAsync(dbIndex, pageSize: 100).WithCancellation(ct))
        {
            keys.Add(new DbObjectInfo(key.ToString(), DbObjectKind.Collection));
            if (keys.Count >= ScanSampleLimit) break;
        }
        return keys.OrderBy(k => k.Name).ToList();
    }

    public async Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        using var mux = await ConnectAsync(c);
        var dbIndex = int.TryParse(container.TrimStart('d', 'b'), out var i) ? i : 0;
        var db = mux.GetDatabase(dbIndex);
        var type = await db.KeyTypeAsync(obj);
        var ttl = await db.KeyTimeToLiveAsync(obj);
        return new List<ColumnInfo>
        {
            new("type: " + type, "", true),
            new("ttl: " + (ttl?.ToString() ?? "∞"), "", true)
        };
    }

    // ---- profiler / admin ----------------------------------------------------------------------

    public async Task<List<QueryLogEntry>> GetServerActivityAsync(SavedConnection c, CancellationToken ct = default)
    {
        using var mux = await ConnectAsync(c);
        var server = mux.GetServer(mux.GetEndPoints()[0]);
        var slowlog = await server.SlowlogGetAsync(50);

        return slowlog.Select(entry => new QueryLogEntry
        {
            Source = QuerySource.ServerActivity,
            Connection = c.Name,
            Database = "slowlog",
            Sql = string.Join(' ', entry.Arguments.Select(a => a.ToString())),
            DurationMs = entry.Duration.TotalMilliseconds,
            RowCount = -1,
            Success = true,
            DedupKey = entry.UniqueId.ToString()
        }).ToList();
    }

    public async Task<List<SessionInfo>> GetSessionsAsync(SavedConnection c, CancellationToken ct = default)
    {
        using var mux = await ConnectAsync(c);
        var server = mux.GetServer(mux.GetEndPoints()[0]);
        var clients = await server.ClientListAsync();
        return clients.Select(cl => new SessionInfo(
            cl.Id.ToString(), string.IsNullOrEmpty(cl.Name) ? null : cl.Name, $"db{cl.Database}",
            cl.ClientType.ToString(), cl.LastCommand, null)).ToList();
    }

    public async Task KillSessionAsync(SavedConnection c, string sessionId, CancellationToken ct = default)
    {
        using var mux = await ConnectAsync(c);
        var db = mux.GetDatabase(DbIndex(c));
        await db.ExecuteAsync("CLIENT", "KILL", "ID", sessionId);
    }

    public async Task<List<UserInfo>> GetUsersAsync(SavedConnection c, CancellationToken ct = default)
    {
        using var mux = await ConnectAsync(c);
        var db = mux.GetDatabase(DbIndex(c));
        try
        {
            var acl = await db.ExecuteAsync("ACL", "LIST");
            return ((RedisResult[])acl!)
                .Select(r => r.ToString() ?? "")
                .Select(line => new UserInfo(
                    line.Split(' ').ElementAtOrDefault(1) ?? line,
                    line.Contains("~* &* +@all"),
                    line))
                .ToList();
        }
        catch
        {
            return new List<UserInfo>();
        }
    }

    public async Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default)
    {
        using var mux = await ConnectAsync(c);
        var server = mux.GetServer(mux.GetEndPoints()[0]);
        var info = await server.InfoAsync("keyspace");
        return info.SelectMany(g => g)
            .Where(kv => kv.Key == container)
            .Select(kv => new TableStats(kv.Key,
                long.TryParse(kv.Value.Split(',')[0].Replace("keys=", ""), out var n) ? n : 0,
                kv.Value))
            .ToList();
    }

    public async Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default)
    {
        using var mux = await ConnectAsync(c);
        var server = mux.GetServer(mux.GetEndPoints()[0]);
        var info = await server.InfoAsync();
        return info.SelectMany(g => g.Select(kv => new ServerVariable($"{g.Key}:{kv.Key}", kv.Value)))
            .OrderBy(v => v.Name)
            .ToList();
    }

    public async Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        using var mux = await ConnectAsync(c);
        var dbIndex = int.TryParse(container.TrimStart('d', 'b'), out var i) ? i : 0;
        var db = mux.GetDatabase(dbIndex);
        var type = await db.KeyTypeAsync(obj);
        var ttl = await db.KeyTimeToLiveAsync(obj);
        return $"# Key: {obj}\n# Type: {type}\n# TTL: {ttl?.ToString() ?? "∞"}\n# Inspect with: INSPECT {obj}";
    }
}
