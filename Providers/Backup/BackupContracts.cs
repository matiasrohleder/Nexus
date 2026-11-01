using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Models;

namespace Nexus.Providers.Backup;

/// <summary>Explorer level where an engine offers Backup/Restore.</summary>
public enum BackupScope
{
    /// <summary>The whole connection: the database file (SQLite, DuckDB) or the server (Redis).</summary>
    Connection,
    /// <summary>A server-level database node (PostgreSQL, SQL Server).</summary>
    Database,
    /// <summary>A container that is a database (MySQL/MariaDB, MongoDB).</summary>
    Container
}

/// <summary>What the backup path points at, which decides the picker the dialog offers.</summary>
public enum BackupLocation
{
    /// <summary>A single file on this machine.</summary>
    LocalFile,
    /// <summary>A folder on this machine (DuckDB's EXPORT DATABASE).</summary>
    LocalFolder,
    /// <summary>A file on the database server's file system (SQL Server BACKUP TO DISK).</summary>
    ServerFile
}

public enum BackupDirection { Backup, Restore }

/// <summary>An output format an engine can write; <see cref="Extension"/> is empty for folders.</summary>
public sealed record BackupFormat(string Id, string Label, string Extension)
{
    public override string ToString() => Label;
}

/// <summary>
/// What to back up / restore into: <see cref="Connection"/> is the profile (or database-pinned
/// clone) to connect with, <see cref="Database"/> the database by name — empty for
/// <see cref="BackupScope.Connection"/>, where the connection itself is the target.
/// </summary>
public sealed record BackupTarget(SavedConnection Connection, string Database);

/// <summary>
/// Whether a direction can run on this machine. <see cref="Tool"/> names the external tool it
/// needs (null = built in), so the dialog can let the user locate it — or pick another copy.
/// </summary>
public sealed record BackupReadiness(bool Ready, string Detail, string? Tool = null)
{
    public static BackupReadiness Ok(string detail, string? tool = null) => new(true, detail, tool);
    public static BackupReadiness Missing(string tool, string installHint) =>
        new(false, $"'{tool}' was not found. {installHint}", tool);
    public static BackupReadiness Unsupported(string reason) => new(false, reason);
}

/// <summary>A backup/restore failure with a message meant for the user (the log has the details).</summary>
public sealed class BackupException(string message) : Exception(message);

/// <summary>
/// One engine's backup/restore implementation, exposed as <see cref="IDatabaseProvider.Backup"/>
/// (null = the engine has none). Engines use their native mechanism when they have one (SQLite's
/// online backup API, DuckDB EXPORT/IMPORT, SQL Server BACKUP/RESTORE) and the vendor's own
/// command-line client otherwise — Nexus never bundles or downloads those. Implementations log
/// progress to the <see cref="IProgress{T}"/> and throw <see cref="BackupException"/> on failure;
/// callers go through <see cref="BackupRunner"/>, which handles partial files and timing.
/// </summary>
public interface IBackupStrategy
{
    BackupScope Scope { get; }
    BackupLocation Location { get; }

    /// <summary>Formats a backup can be written in; the first is the default.</summary>
    IReadOnlyList<BackupFormat> Formats { get; }

    /// <summary>What "Replace existing data" does on this engine (shown under the checkbox).</summary>
    string ReplaceHint { get; }

    /// <summary>Checks that the tools for a direction are available (file-system lookups only, no connection).</summary>
    BackupReadiness Check(BackupDirection direction);

    /// <summary>Default backup path for <see cref="BackupLocation.ServerFile"/> engines (asks the server); null otherwise.</summary>
    Task<string?> SuggestServerPathAsync(BackupTarget target, string fileName, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    Task BackupAsync(BackupTarget target, string path, BackupFormat format, IProgress<string> log, CancellationToken ct);

    /// <summary>
    /// Restores <paramref name="path"/> into the target, creating the database when it doesn't exist.
    /// <paramref name="replace"/> = overwrite what is there (see <see cref="ReplaceHint"/>).
    /// </summary>
    Task RestoreAsync(BackupTarget target, string path, bool replace, IProgress<string> log, CancellationToken ct);
}
