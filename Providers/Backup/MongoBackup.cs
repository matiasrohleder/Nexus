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

    public Task<BackupReadiness> CheckAsync(BackupDirection direction, SavedConnection connection,
        CancellationToken ct = default) =>
        BackupTools.CheckAsync([direction == BackupDirection.Backup ? "mongodump" : "mongorestore"], InstallHint,
            connection, ct);

    public async Task BackupAsync(BackupTarget target, string path, BackupFormat format,
        IProgress<string> log, CancellationToken ct)
    {
        var tool = await BackupTools.ResolveAsync(["mongodump"], InstallHint, target.Connection, log, ct);
        await using var secret = await ConfigFileAsync(tool, target.Connection, ct);
        var args = ConnectionArgs(tool, target.Connection, secret);
        // A bare --archive streams over stdout (how it leaves a container).
        args.AddRange([$"--db={target.Database}", tool.InContainer ? "--archive" : $"--archive={path}", "--gzip"]);
        await BackupTools.RunCheckedAsync(tool, args, log, ct, stdoutFile: tool.InContainer ? path : null);
    }

    public async Task RestoreAsync(BackupTarget target, string path, bool replace,
        IProgress<string> log, CancellationToken ct)
    {
        var tool = await BackupTools.ResolveAsync(["mongorestore"], InstallHint, target.Connection, log, ct);
        await using var secret = await ConfigFileAsync(tool, target.Connection, ct);
        var args = ConnectionArgs(tool, target.Connection, secret);
        args.AddRange([
            tool.InContainer ? "--archive" : $"--archive={path}", "--gzip", "--stopOnError",
            // Whatever database the archive came from, its collections land in the target.
            "--nsFrom=$db$.$coll$", $"--nsTo={target.Database}.$coll$"
        ]);
        if (replace) args.Add("--drop");

        long failed = 0;
        await BackupTools.RunCheckedAsync(tool, args, log, ct, stdinFile: tool.InContainer ? path : null, onLine: line =>
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
    private static async Task<ToolSecretFile?> ConfigFileAsync(ToolInfo tool, SavedConnection c, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(c.Username)) return null;
        var password = (ConnectionStore.GetPassword(c) ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        return await BackupTools.SecretFileAsync(tool, $"password: \"{password}\"\n", ".yaml", ct);
    }

    private static List<string> ConnectionArgs(ToolInfo tool, SavedConnection c, ToolSecretFile? secret)
    {
        var (host, port) = BackupTools.Endpoint(tool, c);
        var args = new List<string> { $"--host={host}", $"--port={port}" };
        if (secret is not null)
            // Same credential source as the driver (MongoProvider): the profile's database.
            args.AddRange([$"--username={c.Username}", $"--authenticationDatabase={c.Database}", $"--config={secret.Path}"]);
        return args;
    }
}
