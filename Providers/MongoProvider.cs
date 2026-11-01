using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Nexus.Models;
using Nexus.Providers.Backup;
using Nexus.Services;

namespace Nexus.Providers;

/// <summary>
/// MongoDB. Containers are databases, objects are collections. The editor accepts mongosh-style
/// queries — db.&lt;collection&gt;.find({...}) / .aggregate([...]) / .count() / .insertOne({...}) /
/// .deleteMany({...}) — or a raw JSON document which is executed as a runCommand.
/// Documents are flattened into grid rows (nested values shown as JSON).
/// </summary>
public sealed partial class MongoProvider : IDatabaseProvider
{
    public string Id => "mongodb";
    public string QueryLanguage => "mongo";
    public string DisplayName => "MongoDB";
    public string BrandColor => "#47A248";
    public int DefaultPort => 27017;
    public string DefaultUsername => "";
    public string DefaultDatabase => "admin";
    public string? ExplainPrefix => null;

    public ProviderCapabilities Capabilities { get; } = new()
    {
        SupportsSql = false,
        SupportsSchemas = false,
        SupportsServerActivity = true,
        SupportsUsers = true,
        SupportsKillSession = true,
        SupportsExplain = false,
        SupportsRowEditing = false
    };

    public IBackupStrategy? Backup { get; } = new MongoBackup();

    [GeneratedRegex(@"^db\.(?<coll>[\w.-]+)\.(?<op>find|aggregate|count|countDocuments|insertOne|insertMany|deleteMany|updateMany)\s*\((?<args>[\s\S]*)\)\s*;?\s*$")]
    private static partial Regex ShellSyntax();

    /// <summary>
    /// Clients with equal settings share one cluster (connection pool + monitors) inside the driver;
    /// it stays open until <see cref="DisconnectAsync"/> unregisters it.
    /// </summary>
    private MongoClient CreateClient(SavedConnection c, bool track = true)
    {
        var settings = new MongoClientSettings
        {
            Server = new MongoServerAddress(c.Host, c.Port),
            ApplicationName = "Nexus",
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ServerSelectionTimeout = TimeSpan.FromSeconds(15)
        };
        var password = ConnectionStore.GetPassword(c);
        if (!string.IsNullOrEmpty(c.Username))
            settings.Credential = MongoCredential.CreateCredential(c.Database, c.Username, password);
        var client = new MongoClient(settings);
        if (track) ConnectionStatus.MarkOpened(c);
        return client;
    }

    public Task DisconnectAsync(SavedConnection c)
    {
        foreach (var profile in ConnectionStatus.Forget(c.Id))
        {
            // MongoClient.Dispose leaves the shared cluster running (driver 3.9); unregister it explicitly.
            try { ClusterRegistry.Instance.UnregisterAndDisposeCluster(CreateClient(profile, track: false).Cluster); }
            catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    public string QuoteIdent(string ident) => ident;
    public string QuoteLiteral(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    /// <summary>Executed with the connection pinned to the container (runCommand on that database).</summary>
    public string? BuildDropContainerSql(string container, bool cascade) => "{ \"dropDatabase\": 1 }";
    public string BuildSelectQuery(string container, string obj, int limit, IReadOnlyList<ColumnInfo>? columns = null) =>
        $"db.{obj}.find({{}})  // first {limit} documents of {container}.{obj}";

    public async Task<(bool ok, string message)> TestConnectionAsync(SavedConnection c, CancellationToken ct = default)
    {
        var client = CreateClient(c, track: false);
        try
        {
            var db = client.GetDatabase(c.Database);
            var build = await db.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: ct);
            return (true, $"Connected — MongoDB {build.GetValue("version", "?")}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            // Don't leave the probe's cluster open, unless the explorer/tabs are sharing it.
            if (!ConnectionStatus.IsConnected(c.Id))
                try { ClusterRegistry.Instance.UnregisterAndDisposeCluster(client.Cluster); } catch { /* best effort */ }
        }
    }

    // ---- query execution ----------------------------------------------------------------

    public async Task<List<QueryResult>> ExecuteAsync(SavedConnection c, string query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new QueryResult();
        int rows = 0;
        bool success = true;
        string? error = null;

        try
        {
            var client = CreateClient(c);
            var db = client.GetDatabase(c.Database);
            var text = StripComments(query).Trim();

            var m = ShellSyntax().Match(text);
            if (m.Success)
            {
                var coll = db.GetCollection<BsonDocument>(m.Groups["coll"].Value);
                var op = m.Groups["op"].Value;
                var args = m.Groups["args"].Value.Trim();

                switch (op)
                {
                    case "find":
                    {
                        var filter = args.Length > 0 ? BsonDocument.Parse(args) : new BsonDocument();
                        var docs = await coll.Find(filter).Limit(500).ToListAsync(ct);
                        FillFromDocuments(result, docs);
                        rows = docs.Count;
                        break;
                    }
                    case "aggregate":
                    {
                        var stages = BsonSerializer(args).Select(v => v.AsBsonDocument).ToArray();
                        PipelineDefinition<BsonDocument, BsonDocument> pipeline = stages;
                        var docs = await coll.Aggregate(pipeline, cancellationToken: ct).ToListAsync(ct);
                        FillFromDocuments(result, docs);
                        rows = docs.Count;
                        break;
                    }
                    case "count" or "countDocuments":
                    {
                        var filter = args.Length > 0 ? BsonDocument.Parse(args) : new BsonDocument();
                        var count = await coll.CountDocumentsAsync(filter, cancellationToken: ct);
                        result.Columns.Add("count");
                        result.Rows.Add(new ResultRow(new object?[] { count }));
                        rows = 1;
                        break;
                    }
                    case "insertOne":
                    {
                        await coll.InsertOneAsync(BsonDocument.Parse(args), cancellationToken: ct);
                        result.RecordsAffected = 1;
                        result.Message = "1 document inserted";
                        break;
                    }
                    case "insertMany":
                    {
                        var docs = BsonSerializer(args).Select(v => v.AsBsonDocument).ToList();
                        await coll.InsertManyAsync(docs, cancellationToken: ct);
                        result.RecordsAffected = docs.Count;
                        result.Message = $"{docs.Count} document(s) inserted";
                        break;
                    }
                    case "deleteMany":
                    {
                        var filter = args.Length > 0 ? BsonDocument.Parse(args) : new BsonDocument();
                        var res = await coll.DeleteManyAsync(filter, ct);
                        result.RecordsAffected = (int)res.DeletedCount;
                        result.Message = $"{res.DeletedCount} document(s) deleted";
                        break;
                    }
                    case "updateMany":
                    {
                        // args: <filter>, <update> — split at top-level comma.
                        var parts = SplitTopLevel(args);
                        if (parts.Count != 2)
                            throw new ArgumentException("updateMany expects (filter, update).");
                        var res = await coll.UpdateManyAsync(
                            BsonDocument.Parse(parts[0]), BsonDocument.Parse(parts[1]), cancellationToken: ct);
                        result.RecordsAffected = (int)res.ModifiedCount;
                        result.Message = $"{res.ModifiedCount} document(s) updated";
                        break;
                    }
                }
            }
            else if (text.StartsWith('{'))
            {
                var response = await db.RunCommandAsync<BsonDocument>(BsonDocument.Parse(text), cancellationToken: ct);
                FillFromDocuments(result, new List<BsonDocument> { response });
                rows = 1;
            }
            else
            {
                throw new ArgumentException(
                    "Unsupported MongoDB query. Use db.<collection>.find({...}) / .aggregate([...]) / " +
                    ".count() / .insertOne({...}) / .insertMany([...]) / .updateMany(f, u) / .deleteMany({...}), " +
                    "or a raw JSON runCommand document.");
            }

            if (result.HasResultSet)
                result.Message = $"{result.Rows.Count} document(s) returned";
            else if (result.Message.Length == 0)
                result.Message = "Command executed";
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
            Database = c.Database,
            Sql = query,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            RowCount = rows,
            Success = success,
            Error = error
        });

        return new List<QueryResult> { result };
    }

    public Task<int> ExecuteTransactionAsync(SavedConnection c, IReadOnlyList<string> statements, CancellationToken ct = default)
        => throw new NotSupportedException("MongoDB provider does not support SQL-style transactions.");

    private static string StripComments(string text) =>
        string.Join('\n', text.Split('\n').Select(l =>
        {
            var idx = l.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? l[..idx] : l;
        }));

    private static List<BsonValue> BsonSerializer(string jsonArray)
    {
        var doc = BsonDocument.Parse($"{{ \"a\": {jsonArray} }}");
        return doc["a"].AsBsonArray.ToList();
    }

    private static List<string> SplitTopLevel(string args)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            var ch = args[i];
            if (ch is '{' or '[') depth++;
            else if (ch is '}' or ']') depth--;
            else if (ch == ',' && depth == 0)
            {
                parts.Add(args[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(args[start..].Trim());
        return parts;
    }

    /// <summary>Documents → grid: union of top-level keys become columns; nested values as JSON.</summary>
    private static void FillFromDocuments(QueryResult result, List<BsonDocument> docs)
    {
        var keys = new List<string>();
        foreach (var doc in docs)
            foreach (var el in doc.Elements)
                if (!keys.Contains(el.Name))
                    keys.Add(el.Name);

        foreach (var k in keys)
            result.Columns.Add(k);

        foreach (var doc in docs)
        {
            var values = new object?[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                if (!doc.TryGetValue(keys[i], out var v)) { values[i] = null; continue; }
                values[i] = v switch
                {
                    BsonNull => null,
                    BsonString s => s.Value,
                    BsonInt32 n => n.Value,
                    BsonInt64 n => n.Value,
                    BsonDouble n => n.Value,
                    BsonBoolean b => b.Value,
                    BsonDateTime d => d.ToUniversalTime(),
                    BsonObjectId o => o.Value.ToString(),
                    _ => v.ToJson()
                };
            }
            result.Rows.Add(new ResultRow(values));
        }
    }

    // ---- introspection --------------------------------------------------------------------

    /// <summary>Containers already are databases — no extra database level.</summary>
    public Task<List<string>> GetDatabasesAsync(SavedConnection c, CancellationToken ct = default)
        => Task.FromResult(new List<string>());

    public async Task<List<string>> GetContainersAsync(SavedConnection c, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var names = await (await client.ListDatabaseNamesAsync(ct)).ToListAsync(ct);
        return names.Where(n => n is not ("local" or "config")).OrderBy(n => n).ToList();
    }

    public async Task<List<DbObjectInfo>> GetObjectsAsync(SavedConnection c, string container, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var db = client.GetDatabase(container);
        var names = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        return names.OrderBy(n => n).Select(n => new DbObjectInfo(n, DbObjectKind.Collection)).ToList();
    }

    public async Task<List<ColumnInfo>> GetColumnsAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        // Infer the "schema" from a sample document.
        var client = CreateClient(c);
        var coll = client.GetDatabase(container).GetCollection<BsonDocument>(obj);
        var sample = await coll.Find(new BsonDocument()).Limit(1).FirstOrDefaultAsync(ct);
        if (sample is null) return new List<ColumnInfo>();

        return sample.Elements
            .Select(el => new ColumnInfo(el.Name, el.Value.BsonType.ToString(), true, el.Name == "_id"))
            .ToList();
    }

    // ---- profiler / admin -------------------------------------------------------------------

    public async Task<List<QueryLogEntry>> GetServerActivityAsync(SavedConnection c, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var admin = client.GetDatabase("admin");
        var res = await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["currentOp"] = 1, ["active"] = true }, cancellationToken: ct);

        var list = new List<QueryLogEntry>();
        foreach (var op in res.GetValue("inprog", new BsonArray()).AsBsonArray)
        {
            var doc = op.AsBsonDocument;
            var command = doc.GetValue("command", BsonNull.Value);
            if (command is BsonNull) continue;
            list.Add(new QueryLogEntry
            {
                Source = QuerySource.ServerActivity,
                Connection = $"{c.Name} ({doc.GetValue("effectiveUsers", "")})",
                Database = doc.GetValue("ns", "").ToString() ?? "",
                Sql = command.ToJson(),
                DurationMs = doc.TryGetValue("microsecs_running", out var us) ? us.ToDouble() / 1000.0 : -1,
                RowCount = -1,
                Success = true,
                DedupKey = doc.GetValue("opid", "").ToString() ?? ""
            });
        }
        return list;
    }

    public async Task<List<SessionInfo>> GetSessionsAsync(SavedConnection c, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var admin = client.GetDatabase("admin");
        var res = await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["currentOp"] = 1 }, cancellationToken: ct);

        var list = new List<SessionInfo>();
        foreach (var op in res.GetValue("inprog", new BsonArray()).AsBsonArray)
        {
            var doc = op.AsBsonDocument;
            list.Add(new SessionInfo(
                doc.GetValue("opid", "").ToString() ?? "",
                doc.GetValue("effectiveUsers", BsonNull.Value) is BsonArray users && users.Count > 0
                    ? users[0].AsBsonDocument.GetValue("user", "").ToString() : null,
                doc.GetValue("ns", "").ToString(),
                doc.GetValue("op", "").ToString(),
                doc.TryGetValue("command", out var cmd) && cmd is not BsonNull ? cmd.ToJson() : null,
                doc.TryGetValue("microsecs_running", out var us) ? us.ToDouble() / 1000.0 : null));
        }
        return list;
    }

    public async Task KillSessionAsync(SavedConnection c, string sessionId, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var admin = client.GetDatabase("admin");
        await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["killOp"] = 1, ["op"] = int.Parse(sessionId) }, cancellationToken: ct);
    }

    public async Task<List<UserInfo>> GetUsersAsync(SavedConnection c, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var admin = client.GetDatabase("admin");
        var res = await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument { ["usersInfo"] = 1 }, cancellationToken: ct);

        return res.GetValue("users", new BsonArray()).AsBsonArray
            .Select(u => u.AsBsonDocument)
            .Select(u => new UserInfo(
                u.GetValue("user", "").ToString() ?? "",
                u.GetValue("roles", new BsonArray()).AsBsonArray
                    .Any(r => r.AsBsonDocument.GetValue("role", "").ToString() == "root"),
                string.Join(", ", u.GetValue("roles", new BsonArray()).AsBsonArray
                    .Select(r => r.AsBsonDocument.GetValue("role", "").ToString()))))
            .ToList();
    }

    public async Task<List<TableStats>> GetTableStatsAsync(SavedConnection c, string container, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var db = client.GetDatabase(container);
        var stats = new List<TableStats>();
        foreach (var name in await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct))
        {
            var res = await db.RunCommandAsync<BsonDocument>(
                new BsonDocument("collStats", name), cancellationToken: ct);
            stats.Add(new TableStats(name,
                res.GetValue("count", 0).ToInt64(),
                FormatBytes(res.GetValue("size", 0).ToInt64())));
        }
        return stats.OrderByDescending(s => s.RowEstimate).ToList();
    }

    public async Task<List<ServerVariable>> GetServerVariablesAsync(SavedConnection c, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var admin = client.GetDatabase("admin");
        var build = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: ct);
        return build.Elements
            .Where(el => el.Value is BsonString or BsonInt32 or BsonInt64 or BsonBoolean or BsonDouble)
            .Select(el => new ServerVariable(el.Name, el.Value.ToString() ?? ""))
            .ToList();
    }

    public async Task<string> GetObjectDdlAsync(SavedConnection c, string container, string obj, CancellationToken ct = default)
    {
        var client = CreateClient(c);
        var coll = client.GetDatabase(container).GetCollection<BsonDocument>(obj);
        var sample = await coll.Find(new BsonDocument()).Limit(1).FirstOrDefaultAsync(ct);
        return sample is null
            ? $"// Collection {container}.{obj} is empty"
            : $"// Sample document from {container}.{obj}\n" +
              sample.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = true });
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (double)(1 << 30):0.0} GB",
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.0} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):0.0} KB",
        _ => $"{bytes} B"
    };
}
