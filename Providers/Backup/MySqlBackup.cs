using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers.Backup;

/// <summary>
/// MySQL / MariaDB via mysqldump (mariadb-dump) and the mysql (mariadb) client. Dumps carry no
/// USE/CREATE DATABASE, so they restore into any database name. The password goes in an
/// owner-only option file (<c>--defaults-extra-file</c>), never on the command line. Both
/// vendors' tools work against both servers; their option dialects differ, so the flavor is
/// detected from <c>--version</c> (Debian's "mysqldump" is MariaDB's, for instance).
/// </summary>
internal sealed class MySqlBackup(MySqlProvider provider, bool preferMariaDbTools) : IBackupStrategy
{
    private string[] DumpTools => preferMariaDbTools ? ["mariadb-dump", "mysqldump"] : ["mysqldump", "mariadb-dump"];
    private string[] ClientTools => preferMariaDbTools ? ["mariadb", "mysql"] : ["mysql", "mariadb"];

    private static string InstallHint => ExternalTools.InstallHint(
        linux: "Install the client: sudo apt install mariadb-client (or default-mysql-client), or sudo dnf install mariadb / mysql.",
        macos: "Install the client: brew install mysql-client (or mariadb).",
        windows: "Install the MySQL Shell/Server client tools or MariaDB's client (they include mysqldump and mysql).");

    public BackupScope Scope => BackupScope.Container;
    public BackupLocation Location => BackupLocation.LocalFile;
    public IReadOnlyList<BackupFormat> Formats { get; } = [new("sql", "SQL script", ".sql")];

    public string ReplaceHint =>
        "Drops and recreates the database before importing, so objects that aren't in the backup are removed too.";

    public BackupReadiness Check(BackupDirection direction) =>
        BackupTools.Check(direction == BackupDirection.Backup ? DumpTools : ClientTools, InstallHint);

    public async Task BackupAsync(BackupTarget target, string path, BackupFormat format,
        IProgress<string> log, CancellationToken ct)
    {
        var tool = await BackupTools.ResolveAsync(DumpTools, InstallHint, log, ct);
        using var secret = OptionFile(target.Connection);

        var args = ConnectionArgs(tool, target.Connection, secret);
        args.AddRange([
            "--single-transaction", "--routines", "--triggers", "--events", "--hex-blob",
            // Tablespaces need the PROCESS privilege (MySQL 8.0.21+) and never matter for a logical restore.
            "--no-tablespaces", "--default-character-set=utf8mb4", "--verbose", $"--result-file={path}"
        ]);
        if (!IsMariaDb(tool) && tool.Version?.Major >= 8)
            // Oracle's 8.x client: skip histogram queries MariaDB/older servers lack, and GTID
            // statements that make the dump fail to load on another server.
            args.AddRange(["--column-statistics=0", "--set-gtid-purged=OFF"]);
        args.Add(target.Database);

        await BackupTools.RunCheckedAsync(tool, args, log, ct);
    }

    public async Task RestoreAsync(BackupTarget target, string path, bool replace,
        IProgress<string> log, CancellationToken ct)
    {
        BackupRunner.EnsureNotGzip(path, "the mysql client");
        var tool = await BackupTools.ResolveAsync(ClientTools, InstallHint, log, ct);

        // Server-level statements from a connection without a default database: it may be the one being dropped.
        var admin = BackupTools.WithDatabase(target.Connection, "");
        var db = provider.QuoteIdent(target.Database);
        if (replace)
        {
            await BackupTools.ExecAsync(provider, admin, $"DROP DATABASE IF EXISTS {db}", ct);
            log.Report($"Dropped database {target.Database}.");
        }
        await BackupTools.ExecAsync(provider, admin, $"CREATE DATABASE IF NOT EXISTS {db}", ct);

        using var secret = OptionFile(target.Connection);
        var args = ConnectionArgs(tool, target.Connection, secret);
        // No --verbose: it echoes every statement of the script, row data included.
        args.AddRange(["--default-character-set=utf8mb4", $"--database={target.Database}"]);
        log.Report($"Importing {System.IO.Path.GetFileName(path)} into {target.Database} …");

        try
        {
            await BackupTools.RunCheckedAsync(tool, args, log, ct, stdinFile: path);
        }
        catch (Exception ex) when (ex is BackupException or OperationCanceledException)
        {
            log.Report("MySQL can't roll back DDL: the database may be partially restored.");
            throw;
        }
    }

    private static bool IsMariaDb(ToolInfo tool) =>
        tool.VersionText.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ||
        System.IO.Path.GetFileName(tool.Path).StartsWith("mariadb", StringComparison.OrdinalIgnoreCase);

    /// <summary>[client] section with the password; must be the first option on the command line.</summary>
    private static SecretFile OptionFile(SavedConnection c)
    {
        var password = ConnectionStore.GetPassword(c) ?? "";
        var escaped = password.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return SecretFile.Create($"[client]\npassword=\"{escaped}\"\n", ".cnf");
    }

    private static List<string> ConnectionArgs(ToolInfo tool, SavedConnection c, SecretFile secret)
    {
        var args = new List<string>
        {
            $"--defaults-extra-file={secret.Path}",
            // "localhost" would otherwise mean the Unix socket, not the host/port of the profile.
            "--protocol=TCP", $"--host={c.Host}", $"--port={c.Port}", $"--user={c.Username}"
        };
        var mode = c.SslMode.ToLowerInvariant();
        if (IsMariaDb(tool))
            args.AddRange(mode switch
            {
                "disable" => ["--skip-ssl"],
                "verifyca" or "verifyfull" => ["--ssl", "--ssl-verify-server-cert"],
                "require" => ["--ssl", "--skip-ssl-verify-server-cert"],
                // Like the driver's "Preferred": encrypt if offered, don't verify the certificate.
                _ => ["--skip-ssl-verify-server-cert"]
            });
        else
            args.Add("--ssl-mode=" + mode switch
            {
                "disable" => "DISABLED",
                "require" => "REQUIRED",
                "verifyca" => "VERIFY_CA",
                "verifyfull" => "VERIFY_IDENTITY",
                _ => "PREFERRED"
            });
        return args;
    }
}
