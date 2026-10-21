using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Providers;
using Nexus.Services;

namespace Nexus;

/// <summary>
/// Generic per-engine verification: connect → create table → insert → select → introspect
/// (containers/objects/columns/PK) → DDL → stats → sessions/activity (if supported) → drop.
/// Run with: dotnet run -- --selftest <providerId>
/// Connection via env: NEXUS_TEST_HOST/PORT/DB/USER/PW or NEXUS_TEST_FILE for file-based.
/// </summary>
public static class SelfTestMatrix
{
    private static int _failures;

    // Engines that need special DDL for the probe table.
    private static readonly Dictionary<string, string> CreateTableSql = new()
    {
        ["clickhouse"] = "CREATE TABLE {T} (id Int32, name String) ENGINE = MergeTree ORDER BY id",
    };

    private const string DefaultCreate = "CREATE TABLE {T} (id INT PRIMARY KEY, name VARCHAR(100))";

    public static async Task<int> RunAsync(string providerId)
    {
        _failures = 0;
        var provider = ProviderRegistry.Get(providerId);
        Console.WriteLine($"== Nexus self-test matrix: {provider.DisplayName} ==");
        QueryProfiler.Instance.DirectDispatch = true;
        QueryProfiler.Instance.IsCapturing = true;

        var conn = BuildConnection(provider);

        if (providerId == "mongodb") return await RunMongoAsync(provider, conn);
        if (providerId == "redis") return await RunRedisAsync(provider, conn);

        // ---- connect ----
        var (ok, msg) = await provider.TestConnectionAsync(conn);
        Check(ok, $"connect ({msg})");
        if (!ok) return Done();

        // ---- create + insert + select ----
        var table = "nexus_selftest";
        var createSql = (CreateTableSql.GetValueOrDefault(providerId) ?? DefaultCreate).Replace("{T}", table);

        await provider.ExecuteAsync(conn, $"DROP TABLE IF EXISTS {table}");
        var create = await provider.ExecuteAsync(conn, createSql);
        Check(!create.Any(r => r.HasError), $"create table ({create.FirstOrDefault()?.Message})");

        var insert = await provider.ExecuteAsync(conn,
            $"INSERT INTO {table} (id, name) VALUES (1, 'alpha'), (2, 'beta'), (3, 'gamma')");
        Check(!insert.Any(r => r.HasError), "insert 3 rows");

        var select = await provider.ExecuteAsync(conn, $"SELECT id, name FROM {table} ORDER BY id");
        Check(select.Count == 1 && select[0].Rows.Count == 3 && select[0].Columns.Count == 2,
              $"select returns 3 rows / 2 cols (got {select.FirstOrDefault()?.Rows.Count}r/{select.FirstOrDefault()?.Columns.Count}c)");
        if (select.Count > 0 && select[0].Rows.Count > 0)
            Check(select[0].Rows[0][1] == "alpha", $"first row value correct ({select[0].Rows[0][1]})");

        // ---- introspection ----
        var containers = await provider.GetContainersAsync(conn);
        Check(containers.Count > 0, $"containers listed ({containers.Count}: {string.Join(",", containers.Take(4))}…)");

        string? foundContainer = null;
        foreach (var container in containers)
        {
            var objs = await provider.GetObjectsAsync(conn, container);
            if (objs.Any(o => o.Name.Equals(table, StringComparison.OrdinalIgnoreCase)))
            {
                foundContainer = container;
                break;
            }
        }
        Check(foundContainer is not null, $"probe table found in container '{foundContainer}'");

        if (foundContainer is not null)
        {
            var tableName = (await provider.GetObjectsAsync(conn, foundContainer))
                .First(o => o.Name.Equals(table, StringComparison.OrdinalIgnoreCase)).Name;

            var cols = await provider.GetColumnsAsync(conn, foundContainer, tableName);
            Check(cols.Count == 2, $"columns introspected ({string.Join(",", cols.Select(x => x.Name))})");
            Check(cols.Any(x => x.IsPrimaryKey), "primary key detected");

            var ddl = await provider.GetObjectDdlAsync(conn, foundContainer, tableName);
            Check(ddl.Contains("CREATE", StringComparison.OrdinalIgnoreCase), "DDL generated");

            var stats = await provider.GetTableStatsAsync(conn, foundContainer);
            Check(stats is not null, $"table stats returned ({stats!.Count})");

            // BuildSelectQuery smoke — star fallback and explicit-column (multi-line) form.
            var preview = await provider.ExecuteAsync(conn, provider.BuildSelectQuery(foundContainer, tableName, 2));
            Check(preview.Count == 1 && preview[0].Rows.Count == 2, "BuildSelectQuery works (LIMIT 2)");

            var prettySql = provider.BuildSelectQuery(foundContainer, tableName, 2, cols);
            Check(!prettySql.Contains('*') && prettySql.Contains('\n')
                  && cols.All(x => prettySql.Contains(x.Name, StringComparison.OrdinalIgnoreCase)),
                  "explicit column list generated (multi-line, no *)");
            var pretty = await provider.ExecuteAsync(conn, prettySql);
            Check(pretty.Count == 1 && pretty[0].Rows.Count == 2 && pretty[0].Columns.Count == cols.Count,
                  "explicit-column SELECT executes");
        }

        // ---- capability-gated surfaces ----
        if (provider.Capabilities.SupportsServerActivity)
        {
            var activity = await provider.GetServerActivityAsync(conn);
            Check(activity is not null, $"server activity ({activity!.Count} entries)");
        }
        if (provider.Capabilities.SupportsKillSession || provider.Capabilities.SupportsServerActivity)
        {
            var sessions = await provider.GetSessionsAsync(conn);
            Check(sessions is not null, $"sessions ({sessions!.Count})");
        }
        if (provider.Capabilities.SupportsUsers)
        {
            var users = await provider.GetUsersAsync(conn);
            Check(users.Count > 0, $"users listed ({users.Count})");
        }
        var vars = await provider.GetServerVariablesAsync(conn);
        Check(vars.Count > 0, $"server variables ({vars.Count})");

        // ---- transactions (data-editor commit path) ----
        if (provider.Capabilities.SupportsRowEditing)
        {
            var n = await provider.ExecuteTransactionAsync(conn, new[]
            {
                $"UPDATE {table} SET name = 'ALPHA-EDITED' WHERE id = 1",
                $"INSERT INTO {table} (id, name) VALUES (4, 'delta')",
                $"DELETE FROM {table} WHERE id = 2"
            });
            Check(n == 3, "transaction committed 3 statements");

            var after = await provider.ExecuteAsync(conn, $"SELECT COUNT(*) FROM {table}");
            Check(after[0].Rows[0][0] == "3", $"row count correct after tx ({after[0].Rows[0][0]})");

            var edited = await provider.ExecuteAsync(conn, $"SELECT name FROM {table} WHERE id = 1");
            Check(edited[0].Rows.Count > 0 && edited[0].Rows[0][0] == "ALPHA-EDITED", "update applied");

            // rollback: second statement fails → first must NOT persist
            try
            {
                await provider.ExecuteTransactionAsync(conn, new[]
                {
                    $"UPDATE {table} SET name = 'SHOULD-NOT-PERSIST' WHERE id = 1",
                    "INSERT INTO nexus_no_such_table_xyz (x) VALUES (1)"
                });
                Check(false, "failing transaction should throw");
            }
            catch
            {
                var check = await provider.ExecuteAsync(conn, $"SELECT name FROM {table} WHERE id = 1");
                Check(check[0].Rows.Count > 0 && check[0].Rows[0][0] == "ALPHA-EDITED",
                      "failed transaction rolled back (no partial write)");
            }
        }

        // ---- error path + cleanup ----
        var err = await provider.ExecuteAsync(conn, "SELECT * FROM nexus_no_such_table_xyz");
        Check(err.Any(r => r.HasError), "invalid query surfaces error");

        // ---- backup (engines with a no-external-tool path) ----
        if (providerId == "duckdb")
        {
            var dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexus-backup-{Guid.NewGuid():N}");
            var backupOk = await Services.BackupService.BackupAsync(conn, dest, _ => { });
            Check(backupOk && System.IO.Directory.Exists(dest) && System.IO.Directory.GetFiles(dest).Length > 0,
                  "EXPORT DATABASE backup produced files");
            try { System.IO.Directory.Delete(dest, true); } catch { }
        }

        var drop = await provider.ExecuteAsync(conn, $"DROP TABLE {table}");
        Check(!drop.Any(r => r.HasError), "drop table");

        return Done();
    }

    // ---- MongoDB-specific checks --------------------------------------------------------

    private static async Task<int> RunMongoAsync(IDatabaseProvider provider, SavedConnection conn)
    {
        var (ok, msg) = await provider.TestConnectionAsync(conn);
        Check(ok, $"connect ({msg})");
        if (!ok) return Done();

        await provider.ExecuteAsync(conn, "db.nexus_selftest.deleteMany({})");

        var ins = await provider.ExecuteAsync(conn,
            "db.nexus_selftest.insertMany([{\"name\":\"alpha\",\"n\":1},{\"name\":\"beta\",\"n\":2},{\"name\":\"gamma\",\"n\":3}])");
        Check(!ins.Any(r => r.HasError) && ins[0].RecordsAffected == 3, $"insertMany 3 docs ({ins[0].Message})");

        var find = await provider.ExecuteAsync(conn, "db.nexus_selftest.find({\"n\": {\"$gte\": 2}})");
        Check(find[0].Rows.Count == 2, $"find with filter returns 2 docs ({find[0].Rows.Count})");
        Check(find[0].Columns.Contains("name"), "documents flattened into columns");

        var agg = await provider.ExecuteAsync(conn,
            "db.nexus_selftest.aggregate([{\"$group\": {\"_id\": null, \"total\": {\"$sum\": \"$n\"}}}])");
        Check(agg[0].Rows.Count == 1 && agg[0].Rows[0][agg[0].Columns.IndexOf("total")] == "6",
              "aggregate $sum works");

        var count = await provider.ExecuteAsync(conn, "db.nexus_selftest.count()");
        Check(count[0].Rows.Count == 1 && count[0].Rows[0][0] == "3", "count() works");

        var containers = await provider.GetContainersAsync(conn);
        Check(containers.Count > 0, $"databases listed ({string.Join(",", containers.Take(4))})");

        var dbName = containers.FirstOrDefault(x => x == conn.Database) ?? conn.Database;
        var objects = await provider.GetObjectsAsync(conn, dbName);
        Check(objects.Any(o => o.Name == "nexus_selftest" && o.Kind == DbObjectKind.Collection),
              "collection found in explorer");

        var cols = await provider.GetColumnsAsync(conn, dbName, "nexus_selftest");
        Check(cols.Any(x => x.Name == "name"), $"sample schema inferred ({string.Join(",", cols.Select(x => x.Name))})");

        var stats = await provider.GetTableStatsAsync(conn, dbName);
        Check(stats.Any(s => s.Name == "nexus_selftest" && s.RowEstimate == 3), "collStats row count");

        var sessions = await provider.GetSessionsAsync(conn);
        Check(sessions.Count > 0, $"currentOp sessions ({sessions.Count})");

        var vars = await provider.GetServerVariablesAsync(conn);
        Check(vars.Any(v => v.Name == "version"), "buildInfo variables");

        var ddl = await provider.GetObjectDdlAsync(conn, dbName, "nexus_selftest");
        Check(ddl.Contains("Sample document"), "sample-document DDL");

        var bad = await provider.ExecuteAsync(conn, "db.nexus_selftest.find({invalid json");
        Check(bad[0].HasError, "invalid query surfaces error");

        var del = await provider.ExecuteAsync(conn, "db.nexus_selftest.deleteMany({})");
        Check(del[0].RecordsAffected == 3, "deleteMany cleanup");

        return Done();
    }

    // ---- Redis-specific checks ------------------------------------------------------------

    private static async Task<int> RunRedisAsync(IDatabaseProvider provider, SavedConnection conn)
    {
        var (ok, msg) = await provider.TestConnectionAsync(conn);
        Check(ok, $"connect ({msg})");
        if (!ok) return Done();

        var set = await provider.ExecuteAsync(conn, "SET nexus:test:str \"hello world\"");
        Check(!set[0].HasError && set[0].Rows[0][0] == "OK", "SET string");

        var get = await provider.ExecuteAsync(conn, "GET nexus:test:str");
        Check(get[0].Rows[0][0] == "hello world", $"GET returns value ({get[0].Rows[0][0]})");

        await provider.ExecuteAsync(conn, "HSET nexus:test:hash field1 v1 field2 v2");
        var inspect = await provider.ExecuteAsync(conn, "INSPECT nexus:test:hash");
        Check(inspect[0].Rows.Count >= 4 && inspect[0].Rows[0][1] == "Hash",
              $"INSPECT shows hash type + fields ({inspect[0].Rows.Count} rows)");

        var containers = await provider.GetContainersAsync(conn);
        Check(containers.Count > 0, $"keyspaces listed ({string.Join(",", containers)})");

        var keys = await provider.GetObjectsAsync(conn, "db0");
        Check(keys.Any(k => k.Name == "nexus:test:str"), $"keys sampled via SCAN ({keys.Count})");

        var sessions = await provider.GetSessionsAsync(conn);
        Check(sessions.Count > 0, $"CLIENT LIST sessions ({sessions.Count})");

        var users = await provider.GetUsersAsync(conn);
        Check(users.Count > 0, $"ACL users ({users.Count})");

        var vars = await provider.GetServerVariablesAsync(conn);
        Check(vars.Count > 0, $"INFO variables ({vars.Count})");

        var activity = await provider.GetServerActivityAsync(conn);
        Check(activity is not null, $"SLOWLOG entries ({activity!.Count})");

        var bad = await provider.ExecuteAsync(conn, "NOTACOMMAND foo");
        Check(bad[0].HasError, "invalid command surfaces error");

        var del = await provider.ExecuteAsync(conn, "DEL nexus:test:str nexus:test:hash");
        Check(del[0].Rows[0][0] == "2", "DEL cleanup");

        return Done();
    }

    private static SavedConnection BuildConnection(IDatabaseProvider provider)
    {
        var conn = new SavedConnection
        {
            Provider = provider.Id,
            Name = $"SelfTest {provider.DisplayName}",
            Host = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("NEXUS_TEST_PORT"), out var p) ? p : provider.DefaultPort,
            Database = Environment.GetEnvironmentVariable("NEXUS_TEST_DB") ?? provider.DefaultDatabase,
            Username = Environment.GetEnvironmentVariable("NEXUS_TEST_USER") ?? provider.DefaultUsername,
            FilePath = Environment.GetEnvironmentVariable("NEXUS_TEST_FILE") ?? "",
            SslMode = "Disable"
        };
        ConnectionStore.SetPassword(conn, Environment.GetEnvironmentVariable("NEXUS_TEST_PW") ?? "");
        return conn;
    }

    private static int Done()
    {
        Console.WriteLine(_failures == 0 ? "\n✅ ALL CHECKS PASSED" : $"\n❌ {_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string label)
    {
        Console.WriteLine($"   [{(condition ? "PASS" : "FAIL")}] {label}");
        if (!condition) _failures++;
    }
}
