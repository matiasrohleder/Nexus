using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers.Backup;

/// <summary>
/// Redis: an RDB snapshot of the whole server via <c>redis-cli --rdb</c> (password through
/// REDISCLI_AUTH, never argv). Restoring can't be done over the network — a server only loads
/// an RDB file at startup — so restore explains the manual steps instead.
/// </summary>
internal sealed class RedisBackup : IBackupStrategy
{
    private static string InstallHint => ExternalTools.InstallHint(
        linux: "Install redis-cli: sudo apt install redis-tools (Debian/Ubuntu) or sudo dnf install redis (Fedora).",
        macos: "Install redis-cli: brew install redis.",
        windows: "Install redis-cli (it ships with Redis for Windows / Memurai).");

    private const string RestoreUnsupported =
        "Redis can't load a snapshot over the network. To restore: stop the server, replace its RDB file " +
        "(CONFIG GET dir / CONFIG GET dbfilename) with the backup, and start it again.";

    public BackupScope Scope => BackupScope.Connection;
    public BackupLocation Location => BackupLocation.LocalFile;
    public IReadOnlyList<BackupFormat> Formats { get; } = [new("rdb", "RDB snapshot", ".rdb")];

    public string ReplaceHint => "";

    public BackupReadiness Check(BackupDirection direction) => direction == BackupDirection.Backup
        ? BackupTools.Check(["redis-cli"], InstallHint)
        : BackupReadiness.Unsupported(RestoreUnsupported);

    public async Task BackupAsync(BackupTarget target, string path, BackupFormat format,
        IProgress<string> log, CancellationToken ct)
    {
        var tool = await BackupTools.ResolveAsync(["redis-cli"], InstallHint, log, ct);
        var c = target.Connection;
        var args = new List<string> { "-h", c.Host, "-p", c.Port.ToString() };
        if (!string.IsNullOrEmpty(c.Username)) args.AddRange(["--user", c.Username]);
        args.AddRange(["--rdb", path]);

        var env = new Dictionary<string, string>();
        var password = ConnectionStore.GetPassword(c);
        if (!string.IsNullOrEmpty(password)) env["REDISCLI_AUTH"] = password;

        await BackupTools.RunCheckedAsync(tool, args, log, ct, env);
    }

    public Task RestoreAsync(BackupTarget target, string path, bool replace,
        IProgress<string> log, CancellationToken ct) => throw new BackupException(RestoreUnsupported);
}
