using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Providers.Backup;

/// <summary>Helpers shared by the strategies that drive an engine's command-line client.</summary>
internal static class BackupTools
{
    /// <summary>
    /// Readiness for a direction that needs one of <paramref name="names"/> (alternatives,
    /// preferred first): installed here, or inside the container that runs the database.
    /// </summary>
    public static async Task<BackupReadiness> CheckAsync(string[] names, string installHint, SavedConnection c,
        CancellationToken ct)
    {
        var tool = await FindAsync(names, c, ct);
        if (tool is null) return BackupReadiness.Missing(names[0], installHint);
        return BackupReadiness.Ok(tool.InContainer
            ? $"Uses {tool} — no local install needed."
            : $"Uses {tool.Name} — {tool.Path}", tool.Name);
    }

    /// <summary>
    /// Picks the executable to run: the one the user located by hand, otherwise the newest copy
    /// installed (a newer client handles older servers; the reverse is what fails). When none is
    /// installed — or the database's own container has a newer major version — the copy inside
    /// that container is used. Logs the choice.
    /// </summary>
    public static async Task<ToolInfo> ResolveAsync(string[] names, string installHint, SavedConnection c,
        IProgress<string> log, CancellationToken ct)
    {
        var tool = await FindAsync(names, c, ct)
                   ?? throw new BackupException($"'{names[0]}' was not found. {installHint}");
        log.Report($"Using {tool}");
        return tool;
    }

    private static async Task<ToolInfo?> FindAsync(string[] names, SavedConnection c, CancellationToken ct)
    {
        ToolInfo? local = null;
        foreach (var name in names)
        {
            var paths = ExternalTools.Locate(name);
            if (paths.Count == 0) continue;
            if (ExternalTools.HasCustomPath(name)) return await ExternalTools.DescribeAsync(paths[0], ct);

            var infos = new List<ToolInfo>();
            foreach (var p in paths) infos.Add(await ExternalTools.DescribeAsync(p, ct));
            local = infos.OrderByDescending(i => i.Version ?? new Version(0, 0)).First();
            break;
        }

        var inContainer = await ContainerTools.FindToolAsync(local is null ? names : [local.Name], c, ct);
        if (local is null) return inContainer;
        // The container's client matches its server; an older local one may not handle it (pg_dump).
        return inContainer?.Version?.Major > local.Version?.Major ? inContainer : local;
    }

    /// <summary>Host and port the tool connects to: the profile's, or the database's own port inside its container.</summary>
    public static (string Host, int Port) Endpoint(ToolInfo tool, SavedConnection c) =>
        tool.Container is { } k ? ("127.0.0.1", k.Port) : (c.Host, c.Port);

    /// <summary>
    /// An owner-only file with a secret for the tool's own config mechanism, where the tool can
    /// read it (on this machine, or inside its container). Dispose deletes it.
    /// </summary>
    public static async Task<ToolSecretFile> SecretFileAsync(ToolInfo tool, string content, string extension,
        CancellationToken ct) => tool.Container is { } k
        ? new ToolSecretFile(await ContainerTools.CreateSecretFileAsync(k, content, extension, ct))
        : new ToolSecretFile(SecretFile.Create(content, extension));

    /// <summary>
    /// Runs a tool (here or in its container) and turns a non-zero exit code into a
    /// <see cref="BackupException"/>. <paramref name="stdinFile"/>/<paramref name="stdoutFile"/>
    /// stream a file through the tool's standard input/output.
    /// </summary>
    public static async Task RunCheckedAsync(ToolInfo tool, IReadOnlyList<string> args, IProgress<string> log,
        CancellationToken ct, IReadOnlyDictionary<string, string>? env = null, string? stdinFile = null,
        Action<string>? onLine = null, string? stdoutFile = null)
    {
        var exit = tool.InContainer
            ? await ContainerTools.RunAsync(tool, args, log, ct, env, stdinFile, stdoutFile, onLine)
            : await ExternalTools.RunAsync(tool.Path, args, log, ct, env, stdinFile, onLine, stdoutFile: stdoutFile);
        if (exit != 0)
            throw new BackupException($"{tool.Name} failed (exit code {exit}) — see the log for details.");
    }

    /// <summary>Runs SQL through the provider and throws its error message, if any.</summary>
    public static async Task<QueryResult> ExecAsync(IDatabaseProvider provider, SavedConnection c, string sql,
        CancellationToken ct)
    {
        var results = await provider.ExecuteAsync(c, sql, ct);
        ct.ThrowIfCancellationRequested();
        var error = results.FirstOrDefault(r => r.HasError)?.Error;
        if (error is not null) throw new BackupException(error);
        return results[0];
    }

    /// <summary>Clone of the target's connection pointed at another database (maintenance work, CREATE/DROP).</summary>
    public static SavedConnection WithDatabase(SavedConnection c, string database)
    {
        var clone = c.Clone();
        clone.Database = database;
        return clone;
    }
}

/// <summary>A secret file on this machine or inside a tool's container; <see cref="Path"/> is as the tool sees it.</summary>
internal sealed class ToolSecretFile : IAsyncDisposable
{
    private readonly SecretFile? _local;
    private readonly ContainerSecretFile? _container;

    public ToolSecretFile(SecretFile local) => _local = local;
    public ToolSecretFile(ContainerSecretFile container) => _container = container;

    public string Path => _local?.Path ?? _container!.Path;

    public async ValueTask DisposeAsync()
    {
        _local?.Dispose();
        if (_container is not null) await _container.DisposeAsync();
    }
}
