using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Providers.Backup;

/// <summary>
/// Runs an engine's <see cref="IBackupStrategy"/> with the guarantees every engine shares:
/// local backups are written to a temporary sibling and only moved over the destination once
/// they succeed — a failed or cancelled run never leaves a truncated file, nor destroys an
/// older backup at the same path — and every run ends with a summary line in the log.
/// </summary>
public static class BackupRunner
{
    private const string PartialSuffix = ".nexus-partial";

    public static async Task BackupAsync(IBackupStrategy strategy, BackupTarget target, string path,
        BackupFormat format, IProgress<string> log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (strategy.Location == BackupLocation.ServerFile)
        {
            await strategy.BackupAsync(target, path, format, log, ct);
            log.Report($"Backup finished in {Elapsed(sw)} — saved on the server at {path}");
            return;
        }

        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new BackupException($"The folder {parent} does not exist.");
        if (strategy.Location == BackupLocation.LocalFolder && Directory.Exists(path) &&
            Directory.EnumerateFileSystemEntries(path).Any())
            throw new BackupException($"{path} already exists and is not empty — choose a new folder name.");

        var partial = path + PartialSuffix;
        DeletePath(partial);
        try
        {
            await strategy.BackupAsync(target, partial, format, log, ct);

            if (strategy.Location == BackupLocation.LocalFolder)
            {
                if (Directory.Exists(path)) Directory.Delete(path); // empty, checked above
                Directory.Move(partial, path);
            }
            else
            {
                if (!File.Exists(partial))
                    throw new BackupException("The backup tool reported success but wrote no file.");
                File.Move(partial, path, overwrite: true);
            }
        }
        catch
        {
            DeletePath(partial);
            throw;
        }

        log.Report($"Backup finished in {Elapsed(sw)} — {Size(path)} saved to {path}");
    }

    public static async Task RestoreAsync(IBackupStrategy strategy, BackupTarget target, string path,
        bool replace, IProgress<string> log, CancellationToken ct)
    {
        if (strategy.Location != BackupLocation.ServerFile)
        {
            path = Path.GetFullPath(path);
            var exists = strategy.Location == BackupLocation.LocalFolder ? Directory.Exists(path) : File.Exists(path);
            if (!exists) throw new BackupException($"{path} does not exist.");
        }

        var sw = Stopwatch.StartNew();
        await strategy.RestoreAsync(target, path, replace, log, ct);
        log.Report($"Restore finished in {Elapsed(sw)}.");
    }

    private static string Elapsed(Stopwatch sw) =>
        sw.Elapsed.TotalSeconds < 60 ? $"{sw.Elapsed.TotalSeconds:0.0} s" : $"{sw.Elapsed:h\\:mm\\:ss}";

    private static string Size(string path)
    {
        long bytes = File.Exists(path)
            ? new FileInfo(path).Length
            : Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                : 0;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private static void DeletePath(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { /* best effort: leftover temp output */ }
    }

    /// <summary>Rejects compressed scripts up front with a clear message instead of a parser error mid-restore.</summary>
    internal static void EnsureNotGzip(string path, string what)
    {
        Span<byte> head = stackalloc byte[2];
        using var f = File.OpenRead(path);
        if (f.Read(head) == 2 && head[0] == 0x1F && head[1] == 0x8B)
            throw new BackupException($"{Path.GetFileName(path)} is gzip-compressed — decompress it first ({what} expects a plain file).");
    }
}
