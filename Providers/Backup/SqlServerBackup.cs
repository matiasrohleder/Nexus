using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Nexus.Models;

namespace Nexus.Providers.Backup;

/// <summary>
/// SQL Server's own BACKUP / RESTORE DATABASE — no external tool. Both run on the server, so the
/// path is on the server's file system (inside the container, for Docker). Backups are COPY_ONLY
/// so an ad-hoc backup never disturbs the server's scheduled backup chain.
/// </summary>
internal sealed class SqlServerBackup(SqlServerProvider provider) : IBackupStrategy
{
    public BackupScope Scope => BackupScope.Database;
    public BackupLocation Location => BackupLocation.ServerFile;
    public IReadOnlyList<BackupFormat> Formats { get; } = [new("bak", "Full backup (.bak)", ".bak")];

    public string ReplaceHint =>
        "Overwrites the existing database (RESTORE … WITH REPLACE), disconnecting everyone using it.";

    public BackupReadiness Check(BackupDirection direction) =>
        BackupReadiness.Ok("Runs on the server with BACKUP/RESTORE DATABASE — the path is on the server's file system.");

    public async Task<string?> SuggestServerPathAsync(BackupTarget target, string fileName, CancellationToken ct)
    {
        var dir = await ScalarAsync(Master(target), "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))", ct);
        return string.IsNullOrEmpty(dir) ? null : Join(dir, fileName);
    }

    public async Task BackupAsync(BackupTarget target, string path, BackupFormat format,
        IProgress<string> log, CancellationToken ct)
    {
        var sql = $"BACKUP DATABASE {provider.QuoteIdent(target.Database)} TO DISK = {N(path)} " +
                  $"WITH COPY_ONLY, INIT, FORMAT, CHECKSUM, STATS = 10, NAME = {N($"Nexus backup of {target.Database}")}";
        await RunAsync(Master(target), sql, log, ct);
    }

    public async Task RestoreAsync(BackupTarget target, string path, bool replace,
        IProgress<string> log, CancellationToken ct)
    {
        var master = Master(target);
        var db = target.Database;
        var files = await QueryAsync(master, $"RESTORE FILELISTONLY FROM DISK = {N(path)}",
            r => (Logical: r.GetString(r.GetOrdinal("LogicalName")), Type: r.GetString(r.GetOrdinal("Type"))), ct);
        if (files.Count == 0) throw new BackupException("The backup file lists no database files.");

        var exists = await ScalarAsync(master, $"SELECT CAST(DB_ID({N(db)}) AS nvarchar(20))", ct) is not null;
        if (exists && !replace)
            throw new BackupException($"Database {db} already exists — tick \"Replace existing data\" to overwrite it.");

        var moves = await PlanFileMovesAsync(master, db, exists, files, ct);
        var restore = $"RESTORE DATABASE {provider.QuoteIdent(db)} FROM DISK = {N(path)} WITH " +
                      string.Join(", ", moves.Select(m => $"MOVE {N(m.Logical)} TO {N(m.Physical)}")) +
                      (replace ? ", REPLACE" : "") + ", RECOVERY, STATS = 10";

        if (!exists)
        {
            await RunAsync(master, restore, log, ct);
            return;
        }

        // Nexus's own pooled sessions count as users of the database too.
        SqlConnection.ClearAllPools();
        await RunAsync(master, $"ALTER DATABASE {provider.QuoteIdent(db)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE", log, ct);
        try
        {
            await RunAsync(master, restore, log, ct);
        }
        finally
        {
            // A successful restore already reset it; after a failure the database must not stay locked.
            try { await RunAsync(master, $"ALTER DATABASE {provider.QuoteIdent(db)} SET MULTI_USER", IgnoreProgress, CancellationToken.None); }
            catch { /* the database may be gone or mid-recovery; the restore error is what matters */ }
            SqlConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// Where each file of the backup goes: over the files of the database being replaced when
    /// there is one, otherwise into the server's default data/log folders named after the target
    /// (the backup's original paths would collide with the database it was taken from).
    /// </summary>
    private async Task<List<(string Logical, string Physical)>> PlanFileMovesAsync(SavedConnection master,
        string db, bool exists, List<(string Logical, string Type)> files, CancellationToken ct)
    {
        var existingData = new Queue<string>();
        var existingLog = new Queue<string>();
        if (exists)
            foreach (var (type, physical) in await QueryAsync(master,
                         $"SELECT type, physical_name FROM sys.master_files WHERE database_id = DB_ID({N(db)}) ORDER BY file_id",
                         r => (Type: Convert.ToInt32(r.GetValue(0)), Physical: r.GetString(1)), ct))
                (type == 1 ? existingLog : existingData).Enqueue(physical);

        var dataDir = await ScalarAsync(master, "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000))", ct) ?? "";
        var logDir = await ScalarAsync(master, "SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000))", ct) ?? dataDir;
        var safeName = string.Concat(db.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));

        var moves = new List<(string, string)>();
        int data = 0, log = 0;
        foreach (var (logical, type) in files)
        {
            string physical;
            if (type == "L")
                physical = existingLog.Count > 0 ? existingLog.Dequeue()
                    : Join(logDir, ++log == 1 ? $"{safeName}_log.ldf" : $"{safeName}_log{log}.ldf");
            else if (type == "D")
                physical = existingData.Count > 0 ? existingData.Dequeue()
                    : Join(dataDir, ++data == 1 ? $"{safeName}.mdf" : $"{safeName}_{data}.ndf");
            else // FILESTREAM / full-text containers are folders
                physical = Join(dataDir, $"{safeName}_{logical}");
            moves.Add((logical, physical));
        }
        return moves;
    }

    private static readonly IProgress<string> IgnoreProgress = Services.IgnoreLog.Instance;

    private static SavedConnection Master(BackupTarget target) => BackupTools.WithDatabase(target.Connection, "master");

    private string N(string value) => "N" + provider.QuoteLiteral(value);

    /// <summary>Joins a server-side folder and file name with the server's separator (Windows or Linux host).</summary>
    private static string Join(string dir, string file)
    {
        var sep = dir.Contains('\\') ? '\\' : '/';
        return dir.TrimEnd('\\', '/') + sep + file;
    }

    /// <summary>Executes a long-running statement, streaming the server's progress messages (STATS) to the log.</summary>
    private async Task RunAsync(SavedConnection c, string sql, IProgress<string> log, CancellationToken ct)
    {
        await using var conn = (SqlConnection)await provider.OpenAsync(c, ct);
        conn.InfoMessage += (_, e) =>
        {
            foreach (SqlError err in e.Errors) log.Report(err.Message);
        };
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        log.Report(sql);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (!ct.IsCancellationRequested)
        {
            // The reason ("Cannot open backup device…") and "…terminating abnormally" arrive as separate errors.
            throw new BackupException(string.Join("\n", ex.Errors.Cast<SqlError>().Select(e => e.Message).Distinct()));
        }
    }

    private async Task<List<T>> QueryAsync<T>(SavedConnection c, string sql, Func<SqlDataReader, T> map, CancellationToken ct)
    {
        try
        {
            await using var conn = (SqlConnection)await provider.OpenAsync(c, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<T>();
            while (await reader.ReadAsync(ct)) list.Add(map(reader));
            return list;
        }
        catch (SqlException ex) when (!ct.IsCancellationRequested)
        {
            throw new BackupException(ex.Message);
        }
    }

    private async Task<string?> ScalarAsync(SavedConnection c, string sql, CancellationToken ct) =>
        (await QueryAsync(c, sql, r => r.IsDBNull(0) ? null : r.GetString(0), ct)).FirstOrDefault();
}
