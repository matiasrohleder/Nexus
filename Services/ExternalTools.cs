using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Services;

/// <summary>
/// A located command-line tool and what its <c>--version</c> printed. With <see cref="Container"/>
/// set it isn't installed here: it is the copy inside the container that runs the database, and
/// <see cref="Path"/> is just its name there (see <see cref="ContainerTools"/>).
/// </summary>
public sealed record ToolInfo(string Path, string VersionText, Version? Version, ContainerRef? Container = null)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public bool InContainer => Container is not null;

    public override string ToString() => Container is { } k
        ? $"{Name}{(Version is null ? "" : $" {Version}")} inside the {k.Runtime} container \"{k.Name}\""
        : Version is null ? Path : $"{Name} {Version} ({Path})";
}

/// <summary>
/// Finds and runs the engines' own command-line clients (pg_dump, mysqldump, mongodump…) used
/// for backup/restore. Nexus never bundles or downloads them: they are looked up in the paths the
/// user picked (<see cref="AppSettings.ToolPaths"/>), then PATH, then each vendor's usual install
/// folders — apps launched from Finder or the Start menu don't inherit the shell's PATH.
/// Secrets never go on the command line: callers pass them through the environment or a
/// <see cref="SecretFile"/>, so the echoed command (and <c>ps</c>) never shows a password.
/// </summary>
public static partial class ExternalTools
{
    private static readonly ConcurrentDictionary<string, (DateTime stamp, ToolInfo info)> Versions = new();

    private static string ExeName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>Every copy of <paramref name="tool"/> found, user-picked path first, then PATH, then install folders.</summary>
    public static IReadOnlyList<string> Locate(string tool)
    {
        var found = new List<string>();
        void Add(string candidate)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(candidate);
                if (File.Exists(full) && !found.Contains(full, PathComparer)) found.Add(full);
            }
            catch { /* malformed PATH entry */ }
        }

        var customPaths = AppSettings.Current.ToolPaths;
        if (customPaths.TryGetValue(tool, out var custom) && !string.IsNullOrWhiteSpace(custom))
            Add(custom);
        // Clients ship together: locating pg_restore by hand also finds the psql next to it.
        foreach (var other in customPaths.Values)
            if (System.IO.Path.GetDirectoryName(other) is { Length: > 0 } dir)
                Add(System.IO.Path.Combine(dir, ExeName(tool)));

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            Add(System.IO.Path.Combine(dir.Trim('"'), ExeName(tool)));

        foreach (var dir in InstallDirectories())
            Add(System.IO.Path.Combine(dir, ExeName(tool)));

        return found;
    }

    /// <summary>True when the user pointed Nexus at a specific executable for this tool.</summary>
    public static bool HasCustomPath(string tool) =>
        AppSettings.Current.ToolPaths.TryGetValue(tool, out var p) && File.Exists(p);

    /// <summary>Remembers an executable the user located by hand (persisted in settings.json).</summary>
    public static void SetCustomPath(string tool, string path)
    {
        AppSettings.Current.ToolPaths[tool] = path;
        AppSettings.Current.Save();
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Where vendors install their clients, newest version first within each family.</summary>
    private static IEnumerable<string> InstallDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     }.Where(p => p.Length > 0).Distinct())
            {
                foreach (var d in Versioned(System.IO.Path.Combine(pf, "PostgreSQL"), "", "bin")) yield return d;
                foreach (var d in Versioned(System.IO.Path.Combine(pf, "MySQL"), "MySQL Server", "bin")) yield return d;
                foreach (var d in Versioned(System.IO.Path.Combine(pf, "MySQL"), "MySQL Workbench", "")) yield return d;
                foreach (var d in Versioned(pf, "MariaDB", "bin")) yield return d;
                foreach (var d in Versioned(System.IO.Path.Combine(pf, "MongoDB", "Tools"), "", "bin")) yield return d;
            }
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (var prefix in new[] { "/opt/homebrew", "/usr/local" })
            {
                yield return $"{prefix}/bin";
                yield return $"{prefix}/opt/libpq/bin";
                foreach (var d in Versioned($"{prefix}/opt", "postgresql@", "bin")) yield return d;
                yield return $"{prefix}/opt/mysql-client/bin";
                yield return $"{prefix}/opt/mariadb/bin";
                yield return $"{prefix}/opt/mongodb-database-tools/bin";
                yield return $"{prefix}/opt/redis/bin";
            }
            foreach (var d in Versioned("/Applications/Postgres.app/Contents/Versions", "", "bin")) yield return d;
            yield return "/usr/local/mysql/bin";
            yield break;
        }

        // Linux / BSD: Debian keeps one folder per PostgreSQL major, RHEL uses /usr/pgsql-N.
        foreach (var d in Versioned("/usr/lib/postgresql", "", "bin")) yield return d;
        foreach (var d in Versioned("/usr", "pgsql-", "bin")) yield return d;
        yield return "/usr/local/bin";
        yield return "/usr/bin";
        yield return "/snap/bin";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0) yield return System.IO.Path.Combine(home, ".local", "bin");
    }

    /// <summary>Subfolders of <paramref name="parent"/> named "{prefix}{version}", highest version first.</summary>
    private static IEnumerable<string> Versioned(string parent, string prefix, string suffix)
    {
        string[] dirs;
        try
        {
            if (!Directory.Exists(parent)) return Array.Empty<string>();
            dirs = Directory.GetDirectories(parent, prefix + "*");
        }
        catch { return Array.Empty<string>(); }

        return dirs
            .OrderByDescending(d => ParseVersion(System.IO.Path.GetFileName(d)[prefix.Length..]) ?? new Version(0, 0))
            .Select(d => suffix.Length == 0 ? d : System.IO.Path.Combine(d, suffix));
    }

    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?|\b(\d+)\b")]
    private static partial Regex VersionPattern();

    /// <summary>First version number in a string ("pg_dump (PostgreSQL) 17.2" → 17.2, "16" → 16.0).</summary>
    public static Version? ParseVersion(string text)
    {
        var m = VersionPattern().Match(text);
        if (!m.Success) return null;
        if (m.Groups[4].Success) return new Version(int.Parse(m.Groups[4].Value), 0);
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>Runs <c>tool --version</c> (cached per file + timestamp) to identify the tool.</summary>
    public static async Task<ToolInfo> DescribeAsync(string path, CancellationToken ct = default)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (Versions.TryGetValue(path, out var cached) && cached.stamp == stamp) return cached.info;

        var output = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await RunAsync(path, new[] { "--version" }, IgnoreLog.Instance, timeout.Token,
                onLine: line => { lock (output) output.AppendLine(line); }, echo: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* hung tool: unknown version */ }

        string text;
        lock (output) text = output.ToString().Trim();
        // "mysqldump  Ver 8.4.1 for Linux" / "mysqldump from 11.4.2-MariaDB": take the product version, not "Ver 10.19".
        var info = new ToolInfo(path, text, ParseToolVersion(text));
        Versions[path] = (stamp, info);
        return info;
    }

    /// <summary>The product version in a tool's <c>--version</c> output.</summary>
    public static Version? ParseToolVersion(string text) => ParseVersion(ProductVersionText(text));

    /// <summary>
    /// Runs a probe (e.g. <c>docker ps</c>) and returns its exit code and combined output; a
    /// tool that can't be started or doesn't answer within <paramref name="timeout"/> gives -1.
    /// </summary>
    public static async Task<(int Exit, string Output)> CaptureAsync(string path, IReadOnlyList<string> args,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var output = new StringBuilder();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            var exit = await RunAsync(path, args, IgnoreLog.Instance, limit.Token,
                onLine: line => { lock (output) output.AppendLine(line); }, echo: false);
            lock (output) return (exit, output.ToString().Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (-1, ""); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (-1, ""); }
    }

    private static string ProductVersionText(string text)
    {
        var m = Regex.Match(text, @"(?:Distrib|from)\s+(\d[\w.\-]*)");
        return m.Success ? m.Groups[1].Value : text;
    }

    /// <summary>OS-specific install instructions for a missing tool.</summary>
    public static string InstallHint(string linux, string macos, string windows) =>
        OperatingSystem.IsWindows() ? windows : OperatingSystem.IsMacOS() ? macos : linux;

    /// <summary>
    /// Runs a tool to completion, streaming every stdout/stderr line to <paramref name="log"/>.
    /// <paramref name="stdinFile"/> is piped to the tool's input (e.g. a SQL script for the mysql
    /// client); otherwise stdin is closed at once, so a tool that would prompt for a password fails
    /// instead of hanging. <paramref name="stdoutFile"/> receives the tool's standard output as
    /// raw bytes (then only stderr is logged). Cancelling kills the whole process tree. Returns the exit code.
    /// </summary>
    public static async Task<int> RunAsync(string path, IReadOnlyList<string> args, IProgress<string> log,
        CancellationToken ct, IReadOnlyDictionary<string, string>? env = null, string? stdinFile = null,
        Action<string>? onLine = null, bool echo = true, string? stdoutFile = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (key, value) in env)
                psi.Environment[key] = value;

        if (echo)
            log.Report("$ " + System.IO.Path.GetFileName(path) + " " + FormatArgs(args, stdinFile, stdoutFile));

        using var process = new Process { StartInfo = psi };
        DataReceivedEventHandler forward = (_, e) =>
        {
            if (e.Data is null) return;
            onLine?.Invoke(e.Data);
            log.Report(e.Data);
        };
        process.OutputDataReceived += forward;
        process.ErrorDataReceived += forward;

        ct.ThrowIfCancellationRequested();
        process.Start();
        Task? copyOut = null;
        if (stdoutFile is null) process.BeginOutputReadLine();
        else copyOut = CopyToFileAsync(process.StandardOutput.BaseStream, stdoutFile);
        process.BeginErrorReadLine();

        using var kill = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        });

        try
        {
            if (stdinFile is not null)
            {
                await using var input = new FileStream(stdinFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1 << 16, useAsync: true);
                await input.CopyToAsync(process.StandardInput.BaseStream, ct);
            }
        }
        catch (IOException) { /* the tool exited early (e.g. SQL error): its exit code tells the story */ }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }

        // Also waits until the redirected output has been fully read.
        await process.WaitForExitAsync(CancellationToken.None);
        if (copyOut is not null) await copyOut;
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>The arguments as echoed in the log, with the redirections a shell would show.</summary>
    public static string FormatArgs(IReadOnlyList<string> args, string? stdinFile = null, string? stdoutFile = null)
    {
        static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
        var text = string.Join(' ', args.Select(Quote));
        if (stdinFile is not null) text += " < " + Quote(stdinFile);
        if (stdoutFile is not null) text += " > " + Quote(stdoutFile);
        return text;
    }

    private static async Task CopyToFileAsync(Stream source, string path)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 16, useAsync: true);
        // No token: killing the process on cancel ends the stream.
        await source.CopyToAsync(file);
    }
}

/// <summary>An <see cref="IProgress{T}"/> that drops every line (probes whose output is parsed, not shown).</summary>
public sealed class IgnoreLog : IProgress<string>
{
    public static readonly IgnoreLog Instance = new();
    public void Report(string value) { }
}

/// <summary>
/// A short-lived file holding a secret for a tool's own config mechanism (mysql
/// <c>--defaults-extra-file</c>, mongodump <c>--config</c>). Created owner-only (0600) from the
/// first byte on Unix — %TEMP% is already per-user on Windows — and deleted on dispose.
/// </summary>
public sealed class SecretFile : IDisposable
{
    public string Path { get; }

    private SecretFile(string path) => Path = path;

    public static SecretFile Create(string content, string extension)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexus-{Guid.NewGuid():N}{extension}");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(content);
        return new SecretFile(path);
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* best effort: temp dir */ }
    }
}
