using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers.Backup;

/// <summary>
/// MongoDB via the MongoDB Database Tools (mongodump / mongorestore), as one gzip archive per
/// database. Restores remap every namespace into the target database, so an archive restores
/// under any name. The password travels in an owner-only <c>--config</c> file.
/// </summary>
internal sealed partial class MongoBackup : IBackupStrategy
{
    private static string InstallHint => ExternalTools.InstallHint(
        linux: "Install the MongoDB Database Tools (package mongodb-database-tools, from mongodb.com/try/download/database-tools).",
        macos: "Install the MongoDB Database Tools: brew install mongodb-database-tools.",
        windows: "Install the MongoDB Database Tools (mongodb.com/try/download/database-tools).");

    public BackupScope Scope => BackupScope.Container;
    public BackupLocation Location => BackupLocation.LocalFile;
    public IReadOnlyList<BackupFormat> Formats { get; } = [new("archive", "Compressed archive (mongorestore)", ".archive.gz")];

    public string ReplaceHint => "Drops each collection before restoring it (mongorestore --drop).";

    public BackupReadiness Check(BackupDirection direction) =>
        BackupTools.Check([direction == BackupDirection.Backup ? "mongodump" : "mongorestore"], InstallHint);

    public async Task BackupAsync(BackupTarget target, string path, BackupFormat format,
        IProgress<string> log, CancellationToken ct)
    {
        var tool = await BackupTools.ResolveAsync(["mongodump"], InstallHint, log, ct);
        using var secret = ConfigFile(target.Connection);
        var args = ConnectionArgs(target.Connection, secret);
        args.AddRange([$"--db={target.Database}", $"--archive={path}", "--gzip"]);
        await BackupTools.RunCheckedAsync(tool, args, log, ct);
    }

    public async Task RestoreAsync(BackupTarget target, string path, bool replace,
        IProgress<string> log, CancellationToken ct)
    {
        var tool = await BackupTools.ResolveAsync(["mongorestore"], InstallHint, log, ct);
        using var secret = ConfigFile(target.Connection);
        var args = ConnectionArgs(target.Connection, secret);
        args.AddRange([
            $"--archive={path}", "--gzip", "--stopOnError",
            // Whatever database the archive came from, its collections land in the target.
            "--nsFrom=$db$.$coll$", $"--nsTo={target.Database}.$coll$"
        ]);
        if (replace) args.Add("--drop");

        long failed = 0;
        await BackupTools.RunCheckedAsync(tool, args, log, ct, onLine: line =>
        {
            var m = FailedDocuments().Match(line);
            if (m.Success) failed = long.Parse(m.Groups[1].Value);
        });
        if (failed > 0)
            throw new BackupException($"{failed} document(s) failed to restore — see the log for details.");
    }

    [GeneratedRegex(@"(\d+) document\(s\) failed to restore")]
    private static partial Regex FailedDocuments();

    /// <summary>YAML config with the password (mongodump/mongorestore --config); null without credentials.</summary>
    private static SecretFile? ConfigFile(SavedConnection c)
    {
        if (string.IsNullOrEmpty(c.Username)) return null;
        var password = (ConnectionStore.GetPassword(c) ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        return SecretFile.Create($"password: \"{password}\"\n", ".yaml");
    }

    private static List<string> ConnectionArgs(SavedConnection c, SecretFile? secret)
    {
        var args = new List<string> { $"--host={c.Host}", $"--port={c.Port}" };
        if (secret is not null)
            // Same credential source as the driver (MongoProvider): the profile's database.
            args.AddRange([$"--username={c.Username}", $"--authenticationDatabase={c.Database}", $"--config={secret.Path}"]);
        return args;
    }
}
