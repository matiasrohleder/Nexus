using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers.Backup;

/// <summary>
/// PostgreSQL via pg_dump / pg_restore / psql. Custom-format archives (the default) restore with
/// pg_restore; plain SQL scripts with psql. Restores run in a single transaction, so a failure
/// leaves the target untouched. pg_dump refuses servers newer than itself, so the newest client
/// installed is used and an outdated one is reported before anything runs.
/// </summary>
internal sealed class PostgresBackup(PostgresProvider provider) : IBackupStrategy
{
    private static readonly BackupFormat Custom = new("custom", "Custom archive (pg_restore)", ".dump");
    private static readonly BackupFormat Plain = new("plain", "Plain SQL script (psql)", ".sql");

    private static string InstallHint => ExternalTools.InstallHint(
        linux: "Install the PostgreSQL client: sudo apt install postgresql-client (Debian/Ubuntu) or sudo dnf install postgresql (Fedora).",
        macos: "Install the PostgreSQL client: brew install libpq, or Postgres.app.",
        windows: "Install PostgreSQL's \"Command Line Tools\" with the EDB installer (postgresql.org/download/windows).");

    public BackupScope Scope => BackupScope.Database;
    public BackupLocation Location => BackupLocation.LocalFile;
    public IReadOnlyList<BackupFormat> Formats { get; } = [Custom, Plain];

    public string ReplaceHint =>
        "Drops the objects in the backup before recreating them (pg_restore --clean). The restore runs " +
        "in one transaction: if anything fails, the database is left as it was.";

    public BackupReadiness Check(BackupDirection direction) => direction == BackupDirection.Backup
        ? BackupTools.Check(["pg_dump"], InstallHint)
        : BackupTools.Check(["pg_restore"], InstallHint);

    public async Task BackupAsync(BackupTarget target, string path, BackupFormat format,
        IProgress<string> log, CancellationToken ct)
    {
        var tool = await BackupTools.ResolveAsync(["pg_dump"], InstallHint, log, ct);
        await EnsureClientNotOlderAsync(tool, target.Connection, ct);

        var args = ConnectionArgs(target.Connection, target.Database);
        args.AddRange(["--verbose", $"--format={format.Id}", $"--file={path}"]);
        // Plain scripts are self-cleaning, so restoring one over an existing database works too.
        if (format == Plain) args.AddRange(["--clean", "--if-exists"]);

        await BackupTools.RunCheckedAsync(tool, args, log, ct, Environment(target.Connection));
    }

    public async Task RestoreAsync(BackupTarget target, string path, bool replace,
        IProgress<string> log, CancellationToken ct)
    {
        var isArchive = IsCustomArchive(path);
        if (!isArchive) BackupRunner.EnsureNotGzip(path, "psql");

        await EnsureDatabaseExistsAsync(target, log, ct);

        var args = ConnectionArgs(target.Connection, target.Database);
        if (isArchive)
        {
            var tool = await BackupTools.ResolveAsync(["pg_restore"], InstallHint, log, ct);
            args.AddRange(["--verbose", "--single-transaction", "--exit-on-error"]);
            if (replace) args.AddRange(["--clean", "--if-exists"]);
            args.Add(path);

            string? missingRole = null;
            try
            {
                await BackupTools.RunCheckedAsync(tool, args, log, ct, Environment(target.Connection),
                    onLine: line => missingRole ??= MissingRole(line));
            }
            catch (BackupException) when (missingRole is not null)
            {
                throw new BackupException(
                    $"The backup assigns objects to role \"{missingRole}\", which doesn't exist on this server. " +
                    "Create the role (CREATE ROLE …) and restore again. Nothing was changed.");
            }
        }
        else
        {
            var tool = await BackupTools.ResolveAsync(["psql"], InstallHint, log, ct);
            if (replace)
                log.Report("Note: a plain SQL script decides itself what it drops (Nexus writes them with --clean).");
            args.AddRange(["--no-psqlrc", "--set=ON_ERROR_STOP=1", "--single-transaction", $"--file={path}"]);
            await BackupTools.RunCheckedAsync(tool, args, log, ct, Environment(target.Connection));
        }
    }

    /// <summary>Custom-format archives start with "PGDMP"; anything else is treated as a SQL script.</summary>
    private static bool IsCustomArchive(string path)
    {
        Span<byte> head = stackalloc byte[5];
        using var f = File.OpenRead(path);
        return f.Read(head) == 5 && Encoding.ASCII.GetString(head) == "PGDMP";
    }

    private static string? MissingRole(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, "role \"([^\"]+)\" does not exist");
        return m.Success ? m.Groups[1].Value : null;
    }

    private async Task EnsureClientNotOlderAsync(ToolInfo tool, SavedConnection c, CancellationToken ct)
    {
        var row = await BackupTools.ExecAsync(provider, c, "SHOW server_version_num", ct);
        if (row.Rows.Count == 0 || !int.TryParse(row.Rows[0][0], out var num)) return;
        var serverMajor = num / 10000;
        if (tool.Version is { } v && v.Major < serverMajor)
            throw new BackupException(
                $"pg_dump {v.Major} can't back up a PostgreSQL {serverMajor} server — pg_dump must be at least " +
                $"as new as the server. Install the PostgreSQL {serverMajor} (or newer) client tools, or use " +
                "\"Locate…\" to pick a newer pg_dump.");
    }

    private async Task EnsureDatabaseExistsAsync(BackupTarget target, IProgress<string> log, CancellationToken ct)
    {
        var admin = MaintenanceConnection(target);
        var exists = await BackupTools.ExecAsync(provider, admin,
            $"SELECT 1 FROM pg_database WHERE datname = {provider.QuoteLiteral(target.Database)}", ct);
        if (exists.Rows.Count > 0) return;

        await BackupTools.ExecAsync(provider, admin, $"CREATE DATABASE {provider.QuoteIdent(target.Database)}", ct);
        log.Report($"Created database {target.Database}.");
    }

    /// <summary>A database to run CREATE DATABASE from — never the target itself, which may not exist yet.</summary>
    private SavedConnection MaintenanceConnection(BackupTarget target)
    {
        var c = target.Connection;
        return c.Database != target.Database && !string.IsNullOrEmpty(c.Database)
            ? c
            : BackupTools.WithDatabase(c, provider.DefaultDatabase);
    }

    private static List<string> ConnectionArgs(SavedConnection c, string database) =>
    [
        $"--host={c.Host}", $"--port={c.Port}", $"--username={c.Username}", "--no-password",
        // As a conninfo string: a database name containing '=' would otherwise be parsed as one.
        $"--dbname=dbname='{database.Replace("\\", "\\\\").Replace("'", "\\'")}'"
    ];

    /// <summary>libpq reads the password and TLS mode from the environment (never argv).</summary>
    private static Dictionary<string, string> Environment(SavedConnection c)
    {
        var env = new Dictionary<string, string>
        {
            ["PGAPPNAME"] = "Nexus",
            ["PGCONNECT_TIMEOUT"] = "15",
            ["PGSSLMODE"] = c.SslMode.ToLowerInvariant() switch
            {
                "disable" => "disable",
                "require" => "require",
                "verifyca" => "verify-ca",
                "verifyfull" => "verify-full",
                _ => "prefer"
            }
        };
        var password = ConnectionStore.GetPassword(c);
        if (!string.IsNullOrEmpty(password)) env["PGPASSWORD"] = password;
        return env;
    }
}
