using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Services;

namespace Nexus.Providers.Backup;

/// <summary>
/// DuckDB's own EXPORT DATABASE / IMPORT DATABASE: a folder with the schema, a load script and
/// one Parquet file per table. IMPORT only creates objects, so "replace" sets the current file
/// aside first (kept next to it as a safety copy, and put back if the import fails).
/// </summary>
internal sealed class DuckDbBackup(DuckDbProvider provider) : IBackupStrategy
{
    public BackupScope Scope => BackupScope.Connection;
    public BackupLocation Location => BackupLocation.LocalFolder;
    public IReadOnlyList<BackupFormat> Formats { get; } = [new("parquet", "Export folder (Parquet)", "")];

    public string ReplaceHint =>
        "Moves the current database file aside (kept as a .pre-restore copy) and imports into a fresh one.";

    public BackupReadiness Check(BackupDirection direction) =>
        BackupReadiness.Ok("Built in: uses DuckDB's EXPORT/IMPORT DATABASE — no external tool needed.");

    public async Task BackupAsync(BackupTarget target, string path, BackupFormat format,
        IProgress<string> log, CancellationToken ct)
    {
        var sql = $"EXPORT DATABASE {provider.QuoteLiteral(path)} (FORMAT PARQUET)";
        log.Report(sql);
        await BackupTools.ExecAsync(provider, target.Connection, sql, ct);
    }

    public async Task RestoreAsync(BackupTarget target, string path, bool replace,
        IProgress<string> log, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(path, "schema.sql")) || !File.Exists(Path.Combine(path, "load.sql")))
            throw new BackupException($"{path} is not a DuckDB export (schema.sql / load.sql missing).");

        var live = target.Connection.FilePath;
        string? aside = null;
        if (replace && File.Exists(live))
        {
            // Nothing may hold the file while it moves (DuckDB keeps it open while any connection is).
            await provider.DisconnectAsync(target.Connection);
            aside = $"{live}.pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(live, aside);
            MoveIfExists(live + ".wal", aside + ".wal");
            log.Report($"Moved the current database aside to {aside}");
        }

        var sql = $"IMPORT DATABASE {provider.QuoteLiteral(path)}";
        log.Report(sql);
        try
        {
            await BackupTools.ExecAsync(provider, target.Connection, sql, ct);
        }
        catch when (aside is not null)
        {
            await provider.DisconnectAsync(target.Connection);
            TryDelete(live);
            TryDelete(live + ".wal");
            File.Move(aside, live);
            MoveIfExists(aside + ".wal", live + ".wal");
            log.Report("Import failed — the original database file was put back.");
            throw;
        }
    }

    private static void MoveIfExists(string from, string to)
    {
        if (File.Exists(from)) File.Move(from, to);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* best effort */ }
    }
}
