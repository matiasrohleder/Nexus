using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Nexus.Providers.Backup;

/// <summary>
/// SQLite's online backup API (built into the driver — no sqlite3 CLI): a consistent page-level
/// copy even while the database is in use. Restore runs the same copy the other way, replacing
/// the whole database in one step.
/// </summary>
internal sealed class SqliteBackup : IBackupStrategy
{
    public BackupScope Scope => BackupScope.Connection;
    public BackupLocation Location => BackupLocation.LocalFile;
    public IReadOnlyList<BackupFormat> Formats { get; } = [new("sqlite", "SQLite database file", ".db")];

    public string ReplaceHint => "Replaces the whole database with the backup.";

    public BackupReadiness Check(BackupDirection direction) =>
        BackupReadiness.Ok("Built in: uses SQLite's online backup API — no external tool needed.");

    public Task BackupAsync(BackupTarget target, string path, BackupFormat format,
        IProgress<string> log, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        log.Report($"Copying {target.Connection.FilePath} …");
        using var source = Open(target.Connection.FilePath, SqliteOpenMode.ReadOnly);
        using var destination = Open(path, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
    }, ct);

    public Task RestoreAsync(BackupTarget target, string path, bool replace,
        IProgress<string> log, CancellationToken ct) => Task.Run(() =>
    {
        if (!IsSqliteFile(path))
            throw new BackupException($"{Path.GetFileName(path)} is not a SQLite database.");

        var live = target.Connection.FilePath;
        if (!replace && File.Exists(live))
        {
            using var check = Open(live, SqliteOpenMode.ReadOnly);
            using var cmd = check.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0)
                throw new BackupException("The database isn't empty — tick \"Replace existing data\" to overwrite it.");
        }

        ct.ThrowIfCancellationRequested();
        // Pooled connections from browsing would keep the old file open.
        SqliteConnection.ClearAllPools();
        log.Report($"Copying {path} into {live} …");
        using var source = Open(path, SqliteOpenMode.ReadOnly);
        using var destination = Open(live, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
    }, ct);

    /// <summary>Unpooled, so the file handle is released as soon as the copy is done.</summary>
    private static SqliteConnection Open(string file, SqliteOpenMode mode)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = mode,
            Pooling = false
        }.ConnectionString);
        conn.Open();
        return conn;
    }

    private static bool IsSqliteFile(string path)
    {
        Span<byte> head = stackalloc byte[16];
        using var f = File.OpenRead(path);
        return f.Read(head) == 16 && Encoding.ASCII.GetString(head) == "SQLite format 3\0";
    }
}
