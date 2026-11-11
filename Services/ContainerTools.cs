using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Models;

namespace Nexus.Services;

/// <summary>
/// A running container that publishes a connection's port: <see cref="Cli"/> is the docker or
/// podman executable, <see cref="Port"/> the database's port inside the container.
/// </summary>
public sealed record ContainerRef(string Cli, string Name, int Port)
{
    public string Runtime => System.IO.Path.GetFileNameWithoutExtension(Cli) switch
    {
        "podman" => "Podman",
        _ => "Docker"
    };
}

/// <summary>
/// Runs an engine's client tools inside the container that hosts the database, for users who
/// run their server in Docker/Podman and never installed the client on the host. Nothing is
/// installed or downloaded: the official images already ship pg_dump, mysqldump, mongodump,
/// redis-cli… The container is found by the published port of a local connection
/// (<c>localhost:5432 → container:5432</c>); inside it the tool connects to 127.0.0.1 on the
/// container's own port. Files can't be passed by path (the container doesn't see the host's
/// file system), so callers stream them through stdin/stdout; secrets travel as forwarded
/// environment variables (<c>docker exec -e NAME</c>, value never on argv) or as a short-lived
/// owner-only file created inside the container.
/// </summary>
public static partial class ContainerTools
{
    private static readonly string[] Clis = ["docker", "podman"];
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private const string PidMarker = "nexus-pid:";

    /// <summary>True for host names that reach this machine, where a published container port answers.</summary>
    public static bool IsLocalHost(string host)
    {
        host = host.Trim().Trim('[', ']');
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var ip) && (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) ||
                                                         ip.Equals(IPAddress.IPv6Any));
    }

    /// <summary>The running container that publishes the connection's port on this machine, if any.</summary>
    public static async Task<ContainerRef?> FindAsync(SavedConnection c, CancellationToken ct)
    {
        if (c.Port <= 0 || !IsLocalHost(c.Host)) return null;
        foreach (var cli in Clis)
        {
            var path = ExternalTools.Locate(cli).FirstOrDefault();
            if (path is null) continue;
            var (exit, output) = await ExternalTools.CaptureAsync(path,
                ["ps", "--format", "{{.Names}}\t{{.Ports}}"], ProbeTimeout, ct);
            if (exit != 0) continue; // no daemon / no permission: as if it weren't installed

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var name = line[..tab];
                foreach (var mapping in line[(tab + 1)..].Split(',', StringSplitOptions.TrimEntries))
                    if (InternalPort(mapping, c.Port) is { } port)
                        return new ContainerRef(path, name, port);
            }
        }
        return null;
    }

    [GeneratedRegex(@"^(?:(?<ip>.*):)?(?<h1>\d+)(?:-(?<h2>\d+))?->(?<c1>\d+)(?:-(?<c2>\d+))?/tcp$")]
    private static partial Regex PortMapping();

    /// <summary>"0.0.0.0:5432->5432/tcp" (or a range) published on a local address → the container port for <paramref name="hostPort"/>.</summary>
    private static int? InternalPort(string mapping, int hostPort)
    {
        var m = PortMapping().Match(mapping);
        if (!m.Success) return null;
        var ip = m.Groups["ip"].Value;
        if (ip.Length > 0 && !IsLocalHost(ip)) return null; // bound to a LAN address only
        var h1 = int.Parse(m.Groups["h1"].Value);
        var h2 = m.Groups["h2"].Success ? int.Parse(m.Groups["h2"].Value) : h1;
        if (hostPort < h1 || hostPort > h2) return null;
        return int.Parse(m.Groups["c1"].Value) + (hostPort - h1);
    }

    /// <summary>
    /// The first of <paramref name="names"/> (preferred first) available inside the container
    /// that hosts <paramref name="c"/>, identified by its <c>--version</c>.
    /// </summary>
    public static async Task<ToolInfo?> FindToolAsync(IEnumerable<string> names, SavedConnection c, CancellationToken ct)
    {
        if (await FindAsync(c, ct) is not { } container) return null;
        foreach (var name in names)
        {
            var (exit, output) = await ExternalTools.CaptureAsync(container.Cli,
                ["exec", container.Name, name, "--version"], ProbeTimeout, ct);
            if (exit == 0 && output.Length > 0)
                return new ToolInfo(name, output, ExternalTools.ParseToolVersion(output), container);
        }
        return null;
    }

    /// <summary>
    /// Runs <paramref name="tool"/> inside its container like <see cref="ExternalTools.RunAsync"/>
    /// runs a local one. <paramref name="env"/> values reach the tool through the docker client's
    /// environment (<c>-e NAME</c>), never argv. Cancelling also kills the process inside the
    /// container — stopping the docker client alone would leave it running.
    /// </summary>
    public static async Task<int> RunAsync(ToolInfo tool, IReadOnlyList<string> args, IProgress<string> log,
        CancellationToken ct, IReadOnlyDictionary<string, string>? env = null, string? stdinFile = null,
        string? stdoutFile = null, Action<string>? onLine = null)
    {
        var k = tool.Container ?? throw new InvalidOperationException($"{tool.Name} doesn't run in a container.");
        var dockerArgs = new List<string> { "exec" };
        if (stdinFile is not null) dockerArgs.Add("-i");
        if (env is not null)
            foreach (var key in env.Keys) dockerArgs.AddRange(["-e", key]);
        // The shell reports the tool's PID (it becomes the tool through exec) so a cancel can kill it.
        dockerArgs.AddRange([k.Name, "sh", "-c", $"echo {PidMarker}$$ >&2; exec \"$@\"", "sh", tool.Name]);
        dockerArgs.AddRange(args);

        log.Report($"$ {tool.Name} {ExternalTools.FormatArgs(args, stdinFile, stdoutFile)}   [in {k.Runtime} container {k.Name}]");

        var pid = 0;
        bool Keep(string line)
        {
            if (!line.StartsWith(PidMarker, StringComparison.Ordinal)) return true;
            int.TryParse(line.AsSpan(PidMarker.Length), out pid);
            return false;
        }
        var filtered = new FilteredLog(log, Keep);
        Action<string>? lines = onLine is null ? null : line => { if (!line.StartsWith(PidMarker, StringComparison.Ordinal)) onLine(line); };

        try
        {
            return await ExternalTools.RunAsync(k.Cli, dockerArgs, filtered, ct, env, stdinFile, lines,
                echo: false, stdoutFile: stdoutFile);
        }
        catch (OperationCanceledException)
        {
            if (pid > 0)
                await ExternalTools.CaptureAsync(k.Cli,
                    ["exec", k.Name, "sh", "-c", "kill -TERM \"$1\"", "sh", pid.ToString()], ProbeTimeout);
            throw;
        }
    }

    /// <summary>
    /// Writes a secret (a tool's option/config file) inside the container, owner-only, through
    /// stdin — never argv. Dispose removes it.
    /// </summary>
    public static async Task<ContainerSecretFile> CreateSecretFileAsync(ContainerRef k, string content,
        string extension, CancellationToken ct)
    {
        var path = $"/tmp/nexus-{Guid.NewGuid():N}{extension}";
        using (var local = SecretFile.Create(content, extension))
        {
            var exit = await ExternalTools.RunAsync(k.Cli,
                ["exec", "-i", k.Name, "sh", "-c", "umask 077 && cat > \"$1\"", "sh", path],
                IgnoreLog.Instance, ct, stdinFile: local.Path, echo: false);
            if (exit != 0)
                throw new IOException($"Could not write a temporary credentials file inside container {k.Name} (exit code {exit}).");
        }
        return new ContainerSecretFile(k, path);
    }

    private sealed class FilteredLog(IProgress<string> inner, Func<string, bool> keep) : IProgress<string>
    {
        public void Report(string value)
        {
            if (keep(value)) inner.Report(value);
        }
    }
}

/// <summary>A secret file inside a container (see <see cref="ContainerTools.CreateSecretFileAsync"/>).</summary>
public sealed class ContainerSecretFile(ContainerRef container, string path) : IAsyncDisposable
{
    public string Path { get; } = path;

    public async ValueTask DisposeAsync() =>
        await ExternalTools.CaptureAsync(container.Cli, ["exec", container.Name, "rm", "-f", Path], TimeSpan.FromSeconds(10));
}
