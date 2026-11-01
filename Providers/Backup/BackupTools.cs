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
    /// <summary>Readiness for a direction that needs one of <paramref name="names"/> (alternatives, preferred first).</summary>
    public static BackupReadiness Check(string[] names, string installHint)
    {
        foreach (var name in names)
        {
            var found = ExternalTools.Locate(name);
            if (found.Count > 0) return BackupReadiness.Ok($"Uses {name} — {found[0]}", name);
        }
        return BackupReadiness.Missing(names[0], installHint);
    }

    /// <summary>
    /// Picks the executable to run: the one the user located by hand, otherwise the newest copy
    /// found (a newer client handles older servers; the reverse is what fails). Logs the choice.
    /// </summary>
    public static async Task<ToolInfo> ResolveAsync(string[] names, string installHint,
        IProgress<string> log, CancellationToken ct)
    {
        foreach (var name in names)
        {
            var paths = ExternalTools.Locate(name);
            if (paths.Count == 0) continue;

            ToolInfo chosen;
            if (ExternalTools.HasCustomPath(name))
                chosen = await ExternalTools.DescribeAsync(paths[0], ct);
            else
            {
                var infos = new List<ToolInfo>();
                foreach (var p in paths) infos.Add(await ExternalTools.DescribeAsync(p, ct));
                chosen = infos.OrderByDescending(i => i.Version ?? new Version(0, 0)).First();
            }
            log.Report($"Using {chosen}");
            return chosen;
        }
        throw new BackupException($"'{names[0]}' was not found. {installHint}");
    }

    /// <summary>Runs a tool and turns a non-zero exit code into a <see cref="BackupException"/>.</summary>
    public static async Task RunCheckedAsync(ToolInfo tool, IReadOnlyList<string> args, IProgress<string> log,
        CancellationToken ct, IReadOnlyDictionary<string, string>? env = null, string? stdinFile = null,
        Action<string>? onLine = null)
    {
        var exit = await ExternalTools.RunAsync(tool.Path, args, log, ct, env, stdinFile, onLine);
        if (exit != 0)
            throw new BackupException(
                $"{System.IO.Path.GetFileNameWithoutExtension(tool.Path)} failed (exit code {exit}) — see the log for details.");
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
