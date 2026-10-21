using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Providers;

namespace Nexus.Services;

/// <summary>
/// Backup/restore via each engine's external tool (pg_dump, mysqldump, …) when it is present
/// in PATH. Engines without a practical CLI path report a hint instead.
/// </summary>
public static class BackupService
{
    public sealed record BackupPlan(bool Available, string ToolName, string Hint);

    /// <summary>Describes whether backup is possible for this connection and with which tool.</summary>
    public static BackupPlan Describe(SavedConnection c)
    {
        var provider = ProviderRegistry.For(c);
        return provider.Id switch
        {
            "postgresql" => Plan("pg_dump", "Full SQL dump via pg_dump."),
            "mysql" or "mariadb" => Plan(FindTool("mariadb-dump", "mysqldump") ?? "mysqldump", "SQL dump via mysqldump/mariadb-dump."),
            "sqlite" => Plan("sqlite3", "File-level backup via sqlite3 .backup."),
            "mongodb" => Plan("mongodump", "BSON dump via mongodump."),
            "redis" => Plan("redis-cli", "RDB snapshot via redis-cli --rdb."),
            "duckdb" => new BackupPlan(true, "(built-in)", "Uses DuckDB's native EXPORT DATABASE — no external tool needed."),
            "clickhouse" => new BackupPlan(false, "clickhouse", "ClickHouse BACKUP requires server-side configuration; use the query editor: BACKUP DATABASE x TO Disk(...)."),
            "sqlserver" => new BackupPlan(false, "sqlcmd", "Use the query editor: BACKUP DATABASE [db] TO DISK = '/path.bak' (path is on the server)."),
            "oracle" => new BackupPlan(false, "expdp", "Oracle Data Pump (expdp) runs server-side; not automated by Nexus."),
            _ => new BackupPlan(false, "", "Backup not supported for this engine.")
        };

        static BackupPlan Plan(string tool, string hint)
        {
            var found = FindTool(tool) is not null;
            return new BackupPlan(found, tool, found ? hint : $"{hint} — '{tool}' not found in PATH; install it to enable backup.");
        }
    }

    private static string? FindTool(params string[] names)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var name in names)
            if (paths.Any(dir => File.Exists(Path.Combine(dir, name))))
                return name;
        return null;
    }

    /// <summary>Runs the backup, streaming progress lines to <paramref name="onOutput"/>.</summary>
    public static async Task<bool> BackupAsync(SavedConnection c, string destination,
        Action<string> onOutput, CancellationToken ct = default)
    {
        var provider = ProviderRegistry.For(c);
        var password = ConnectionStore.GetPassword(c);

        switch (provider.Id)
        {
            case "postgresql":
                return await RunToolAsync("pg_dump",
                    new[] { "-h", c.Host, "-p", c.Port.ToString(), "-U", c.Username, "-d", c.Database, "-f", destination },
                    env: ("PGPASSWORD", password), onOutput, ct);

            case "mysql" or "mariadb":
            {
                var tool = FindTool("mariadb-dump", "mysqldump") ?? "mysqldump";
                return await RunToolAsync(tool,
                    new[] { "-h", c.Host, "-P", c.Port.ToString(), "-u", c.Username, $"-p{password}",
                            "--result-file", destination, c.Database },
                    env: null, onOutput, ct);
            }

            case "sqlite":
                return await RunToolAsync("sqlite3",
                    new[] { c.FilePath, $".backup '{destination}'" }, env: null, onOutput, ct);

            case "mongodb":
                return await RunToolAsync("mongodump",
                    new[] { "--host", c.Host, "--port", c.Port.ToString(), "--db", c.Database, "--out", destination }
                        .Concat(string.IsNullOrEmpty(c.Username)
                            ? Array.Empty<string>()
                            : new[] { "-u", c.Username, "-p", password, "--authenticationDatabase", c.Database })
                        .ToArray(),
                    env: null, onOutput, ct);

            case "redis":
                return await RunToolAsync("redis-cli",
                    new[] { "-h", c.Host, "-p", c.Port.ToString() }
                        .Concat(string.IsNullOrEmpty(password) ? Array.Empty<string>() : new[] { "-a", password })
                        .Concat(new[] { "--rdb", destination })
                        .ToArray(),
                    env: null, onOutput, ct);

            case "duckdb":
            {
                onOutput($"EXPORT DATABASE '{destination}' …");
                var res = await provider.ExecuteAsync(c, $"EXPORT DATABASE '{destination.Replace("'", "''")}'", ct);
                var error = res.FirstOrDefault(r => r.HasError)?.Error;
                onOutput(error ?? "Export completed.");
                return error is null;
            }

            default:
                onOutput(Describe(c).Hint);
                return false;
        }
    }

    private static async Task<bool> RunToolAsync(string tool, string[] args,
        (string key, string value)? env, Action<string> onOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) psi.Environment[env.Value.key] = env.Value.value;

        onOutput($"$ {tool} {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

        try
        {
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            foreach (var line in (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(50))
                onOutput(line);
            foreach (var line in (await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(50))
                onOutput(line);

            onOutput(process.ExitCode == 0 ? "✔ Backup completed." : $"✘ Exited with code {process.ExitCode}.");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            onOutput($"✘ {ex.Message}");
            return false;
        }
    }
}
