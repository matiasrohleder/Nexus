using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Services;

namespace Nexus;

/// <summary>
/// Headless end-to-end verification of the data layer (encryption, connection persistence,
/// query execution, schema introspection, profiler, server activity). Run with:
///   dotnet run -- --selftest
/// Expects a PostgreSQL reachable via the NEXUS_TEST_* environment variables.
/// </summary>
public static class SelfTest
{
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("== Nexus self-test ==");
        QueryProfiler.Instance.DirectDispatch = true; // headless: no UI message pump
        QueryProfiler.Instance.IsCapturing = true;    // profiler is opt-in in the app; tests need it on

        // ---- 1. Encryption round-trip ----
        var enc = CryptoService.Encrypt("s3cr3t-pw");
        Check(enc != "s3cr3t-pw" && enc.Length > 0, "password is encrypted (not plaintext)");
        Check(CryptoService.Decrypt(enc) == "s3cr3t-pw", "password decrypts back to original");

        // ---- 2. Build a connection ----
        var conn = new SavedConnection
        {
            Name = "SelfTest Demo",
            Host = Environment.GetEnvironmentVariable("NEXUS_TEST_HOST") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("NEXUS_TEST_PORT"), out var p) ? p : 55432,
            Database = Environment.GetEnvironmentVariable("NEXUS_TEST_DB") ?? "demo",
            Username = Environment.GetEnvironmentVariable("NEXUS_TEST_USER") ?? "postgres",
            SslMode = "Disable"
        };
        ConnectionStore.SetPassword(conn, Environment.GetEnvironmentVariable("NEXUS_TEST_PW") ?? "nexus123");

        // ---- 3. Persist & reload (backup/restore the real store so we don't pollute it) ----
        var storePath = AppPaths.ConnectionsFile;
        var backup = File.Exists(storePath) ? File.ReadAllText(storePath) : null;
        try
        {
            var store = new ConnectionStore();
            store.SaveAll(new[] { conn });
            var reloaded = store.Load();
            Check(reloaded.Count == 1, "connection persisted and reloaded");
            Check(!reloaded[0].EncryptedPassword.Contains("nexus123"), "stored password is not plaintext on disk");
            Check(ConnectionStore.GetPassword(reloaded[0]) ==
                  (Environment.GetEnvironmentVariable("NEXUS_TEST_PW") ?? "nexus123"),
                  "reloaded connection decrypts to correct password");
        }
        finally
        {
            if (backup is not null) File.WriteAllText(storePath, backup);
            else File.Delete(storePath);
        }

        var provider = Providers.ProviderRegistry.For(conn);

        // ---- 4. Test connection ----
        var (ok, msg) = await provider.TestConnectionAsync(conn);
        Check(ok, $"TestConnection succeeded ({msg})");

        // ---- 5. SELECT with a result set ----
        var res = await provider.ExecuteAsync(conn,
            "SELECT id, name, balance FROM customers ORDER BY id LIMIT 5;");
        Check(res.Count == 1 && res[0].Columns.Count == 3, "SELECT returned 3 columns");
        Check(res[0].Rows.Count == 5, "SELECT returned 5 rows");
        if (res[0].Rows.Count > 0)
            Console.WriteLine($"   first row: id={res[0].Rows[0][0]}, name={res[0].Rows[0][1]}, balance={res[0].Rows[0][2]}");

        // ---- 6. Non-query (records affected) ----
        var ins = await provider.ExecuteAsync(conn,
            "CREATE TEMP TABLE _nexus_t(x int); INSERT INTO _nexus_t VALUES (1),(2),(3);");
        Check(ins.Any(r => r.RecordsAffected == 3 || r.Message.Contains("3")),
              "INSERT reported rows affected");

        // ---- 7. Schema introspection ----
        var databases = await provider.GetDatabasesAsync(conn);
        Check(databases.Contains("demo") && databases.Contains("postgres"),
              $"server databases listed ({string.Join(",", databases)})");
        Check(databases.FirstOrDefault() == conn.Database, "connected database listed first");

        // Browsing another database via a pinned clone (what the explorer tree does).
        var pinned = conn.Clone();
        pinned.Database = "postgres";
        var pinnedSchemas = await provider.GetContainersAsync(pinned);
        Check(pinnedSchemas.Contains("public"), "pinned clone browses another database");

        var schemas = await provider.GetContainersAsync(conn);
        Check(schemas.Contains("public") && schemas.Contains("sales"), "schemas listed (public, sales)");
        var tables = await provider.GetObjectsAsync(conn, "public");
        Check(tables.Any(t => t.Name == "customers" && t.Kind == Providers.DbObjectKind.Table), "table 'customers' found");
        Check(tables.Any(t => t.Name == "big_spenders" && t.Kind == Providers.DbObjectKind.View), "view 'big_spenders' found");
        var cols = await provider.GetColumnsAsync(conn, "public", "customers");
        Check(cols.Count >= 5, $"customers has {cols.Count} columns");
        Check(cols.Any(x => x.Name == "id" && x.IsPrimaryKey), "primary key detected on customers.id");

        // ---- 7b. DROP with a dependent object → error, then CASCADE succeeds ----
        await provider.ExecuteAsync(conn, "DROP TABLE IF EXISTS _nx_parent CASCADE");
        await provider.ExecuteAsync(conn, "CREATE TABLE _nx_parent(id int primary key)");
        await provider.ExecuteAsync(conn, "CREATE VIEW _nx_child AS SELECT * FROM _nx_parent");

        var blocked = await provider.ExecuteAsync(conn, "DROP TABLE _nx_parent");
        Check(blocked.Any(r => r.HasError) &&
              (blocked[0].Error?.Contains("depend", StringComparison.OrdinalIgnoreCase) ?? false),
              "DROP blocked by dependency surfaces an error (not silent)");

        var cascade = await provider.ExecuteAsync(conn, "DROP TABLE _nx_parent CASCADE");
        Check(!cascade.Any(r => r.HasError), "DROP … CASCADE succeeds");
        var gone = await provider.GetObjectsAsync(conn, "public");
        Check(gone.All(o => o.Name != "_nx_parent" && o.Name != "_nx_child"), "dropped objects gone from explorer");

        // ---- 8. Server activity (pg_stat_activity) ----
        var activity = await provider.GetServerActivityAsync(conn);
        Check(activity.All(a => a.Source == QuerySource.ServerActivity),
              $"server activity snapshot returned {activity.Count} backend(s)");

        // ---- 8b. Admin surface (sessions, users, stats, variables, DDL) ----
        var sessions = await provider.GetSessionsAsync(conn);
        Check(sessions is not null, $"sessions listed ({sessions!.Count})");
        var users = await provider.GetUsersAsync(conn);
        Check(users.Any(u => u.Name == conn.Username), "users include the connected role");
        var stats = await provider.GetTableStatsAsync(conn, "public");
        Check(stats.Any(s => s.Name == "customers"), "table stats include customers");
        var vars = await provider.GetServerVariablesAsync(conn);
        Check(vars.Any(v => v.Name == "server_version"), "server variables listed");
        var ddl = await provider.GetObjectDdlAsync(conn, "public", "customers");
        Check(ddl.Contains("CREATE TABLE") && ddl.Contains("PRIMARY KEY"), "DDL script generated");

        // ---- 9. Error path ----
        var err = await provider.ExecuteAsync(conn, "SELECT * FROM table_that_does_not_exist;");
        Check(err.Count == 1 && err[0].HasError, "invalid query surfaces an error result");

        // ---- 10. Profiler captured everything ----
        var appEntries = QueryProfiler.Instance.Entries.Count(e => e.Source == QuerySource.Application);
        Check(appEntries >= 3, $"profiler recorded {appEntries} application queries");
        var failed = QueryProfiler.Instance.Entries.Count(e => !e.Success);
        Check(failed >= 1, $"profiler recorded {failed} failed query");

        Console.WriteLine(_failures == 0
            ? "\n✅ ALL CHECKS PASSED"
            : $"\n❌ {_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string label)
    {
        Console.WriteLine($"   [{(condition ? "PASS" : "FAIL")}] {label}");
        if (!condition) _failures++;
    }
}
