using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Services;

/// <summary>How this copy of Nexus was installed — decides which release asset updates it and how.</summary>
public enum InstallKind
{
    /// <summary>Not from an installer (dotnet run, dist/, unpacked somewhere): can only open the download page.</summary>
    Manual,
    /// <summary>Inno Setup install (unins000.exe next to Nexus.exe).</summary>
    WindowsSetup,
    /// <summary>A Nexus.app bundle (from the .dmg).</summary>
    MacApp,
    /// <summary>/opt/nexus owned by dpkg.</summary>
    LinuxDeb,
    /// <summary>/opt/nexus owned by rpm.</summary>
    LinuxRpm,
    /// <summary>Per-user install.sh from the .tar.gz ($XDG_DATA_HOME/nexus/app).</summary>
    LinuxTarball
}

public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

public sealed record ReleaseInfo(string Version, string Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// Self-update from GitHub Releases: finds the latest release, picks the asset matching how this
/// copy was installed, downloads + verifies it (SHA-256) and installs it in place, then relaunches.
/// Each install kind upgrades through its own package manager, so the OS still sees one installed
/// package (no side-by-side copies, no "already installed" dead end).
/// </summary>
public static class Updater
{
    public const string Repo = "matiasrohleder/Nexus";

    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    /// <summary>Release feed; NEXUS_UPDATE_FEED overrides it (self-tests, trying a fake release).</summary>
    private static string FeedUrl =>
        Environment.GetEnvironmentVariable("NEXUS_UPDATE_FEED") is { Length: > 0 } feed
            ? feed
            : $"https://api.github.com/repos/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // No client-wide timeout: downloads are ~100 MB; the API call gets its own below.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Nexus/{AppInfo.Version}");
        return http;
    }

    public static InstallKind Kind { get; } = Detect(AppContext.BaseDirectory);

    // ---- release feed ---------------------------------------------------------------------

    public static async Task<ReleaseInfo> GetLatestAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        return Parse(await response.Content.ReadAsStringAsync(cts.Token));
    }

    /// <summary>Parses a GitHub "release" object (the /releases/latest response).</summary>
    public static ReleaseInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list))
        {
            foreach (var a in list.EnumerateArray())
            {
                var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
                assets.Add(new ReleaseAsset(
                    a.GetProperty("name").GetString() ?? "",
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest["sha256:".Length..].ToLowerInvariant()
                        : null));
            }
        }
        return new ReleaseInfo(
            tag.TrimStart('v', 'V'),
            CleanNotes(root.TryGetProperty("body", out var b) ? b.GetString() : null),
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage,
            assets);
    }

    /// <summary>GitHub's generated notes are Markdown; the dialog shows plain text.</summary>
    private static string CleanNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = body.Replace("\r", "").Split('\n')
            .Where(l => !l.StartsWith("Download the installer for your system", StringComparison.Ordinal))
            .Select(l => l.TrimStart('#', ' ') is var t && l.StartsWith('#') ? t : l)
            .Select(l => l.StartsWith("* ") || l.StartsWith("- ") ? "•" + l[1..] : l)
            .Select(l => l.Replace("**", ""));
        return string.Join('\n', lines).Trim();
    }

    /// <summary>Semantic-version comparison ("v" prefix ignored; 1.0.0-beta &lt; 1.0.0).</summary>
    public static int CompareVersions(string a, string b)
    {
        static (Version core, string pre) Split(string v)
        {
            v = v.Trim().TrimStart('v', 'V').Split('+')[0];
            var dash = v.IndexOf('-');
            var core = dash >= 0 ? v[..dash] : v;
            var pre = dash >= 0 ? v[(dash + 1)..] : "";
            var parts = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
            while (parts.Count < 3) parts.Add(0);
            return (new Version(parts[0], parts[1], parts[2]), pre);
        }

        var (ca, pa) = Split(a);
        var (cb, pb) = Split(b);
        var c = ca.CompareTo(cb);
        if (c != 0) return c;
        if (pa == pb) return 0;
        if (pa.Length == 0) return 1;   // a release beats its pre-releases
        if (pb.Length == 0) return -1;
        return string.CompareOrdinal(pa, pb);
    }

    public static bool IsNewer(string candidate, string current) => CompareVersions(candidate, current) > 0;

    // ---- install detection ----------------------------------------------------------------

    public static InstallKind Detect(string baseDir)
    {
        try
        {
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir));
            if (OperatingSystem.IsWindows())
                return File.Exists(Path.Combine(dir, "unins000.exe")) ? InstallKind.WindowsSetup : InstallKind.Manual;
            if (OperatingSystem.IsMacOS())
                return BundleOf(dir) is not null ? InstallKind.MacApp : InstallKind.Manual;
            if (OperatingSystem.IsLinux())
            {
                if (dir == "/opt/nexus")
                {
                    if (RunQuiet("dpkg-query", "-S", "/opt/nexus/Nexus")) return InstallKind.LinuxDeb;
                    if (RunQuiet("rpm", "-qf", "/opt/nexus/Nexus")) return InstallKind.LinuxRpm;
                    return InstallKind.Manual;
                }
                if (dir == Path.TrimEndingDirectorySeparator(TarballAppDir)) return InstallKind.LinuxTarball;
            }
        }
        catch { /* unknown layout → manual */ }
        return InstallKind.Manual;
    }

    /// <summary>Where packaging/linux/install.sh puts the app (same rule as the script).</summary>
    private static string TarballAppDir =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "nexus", "app");

    /// <summary>"/Applications/Nexus.app" for ".../Nexus.app/Contents/MacOS", otherwise null.</summary>
    public static string? BundleOf(string baseDir)
    {
        var macos = Path.TrimEndingDirectorySeparator(baseDir);
        var contents = Path.GetDirectoryName(macos);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return Path.GetFileName(macos) == "MacOS" && Path.GetFileName(contents) == "Contents" &&
               bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? bundle
            : null;
    }

    public static string Describe(InstallKind kind) => kind switch
    {
        InstallKind.WindowsSetup => "Windows installer",
        InstallKind.MacApp => "macOS app",
        InstallKind.LinuxDeb => ".deb package",
        InstallKind.LinuxRpm => ".rpm package",
        InstallKind.LinuxTarball => "per-user install (.tar.gz)",
        _ => "not installed by an installer"
    };

    /// <summary>The release file that updates <paramref name="kind"/> (names from packaging/*/build.*).</summary>
    public static string? AssetNameFor(InstallKind kind, string version, Architecture arch)
    {
        var a = arch switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };
        if (a is null) return null;
        return kind switch
        {
            InstallKind.WindowsSetup => $"Nexus-{version}-windows-x64-setup.exe",
            InstallKind.MacApp => $"Nexus-{version}-macos-{a}.dmg",
            InstallKind.LinuxDeb => $"Nexus-{version}-linux-{a}.deb",
            InstallKind.LinuxRpm => $"Nexus-{version}-linux-{a}.rpm",
            InstallKind.LinuxTarball => $"Nexus-{version}-linux-{a}.tar.gz",
            _ => null
        };
    }

    /// <summary>The asset this copy can install itself from, or null (manual download).</summary>
    public static ReleaseAsset? AssetFor(ReleaseInfo release) =>
        AssetNameFor(Kind, release.Version, RuntimeInformation.ProcessArchitecture) is { } name
            ? release.Assets.FirstOrDefault(a => a.Name == name)
            : null;

    // ---- download -------------------------------------------------------------------------

    /// <summary>Downloads <paramref name="asset"/> into a temp folder and verifies its SHA-256.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, ReleaseAsset asset,
        IProgress<double>? progress, CancellationToken ct)
    {
        var expected = asset.Sha256 ?? await ChecksumFromSumsFileAsync(release, asset.Name, ct)
            ?? throw new InvalidOperationException($"The release publishes no checksum for {asset.Name}; refusing to install it.");

        var dir = Path.Combine(Path.GetTempPath(), $"nexus-update-{release.Version}");
        Directory.CreateDirectory(dir);
        // apt/dnf run as root and read the package from here: keep it readable (no _apt warnings).
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                      UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var path = Path.Combine(dir, asset.Name);

        // Already downloaded earlier (e.g. the install step failed and the user retries)?
        if (File.Exists(path) && await Sha256Async(path, ct) == expected)
        {
            progress?.Report(1);
            return path;
        }

        var partial = path + ".part";
        using (var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(partial);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        var actual = await Sha256Async(partial, ct);
        if (actual != expected)
        {
            File.Delete(partial);
            throw new InvalidOperationException($"Checksum mismatch for {asset.Name} (expected {expected}, got {actual}). The download was discarded.");
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>Older releases may lack per-asset digests; the workflow always publishes SHA256SUMS.txt.</summary>
    private static async Task<string?> ChecksumFromSumsFileAsync(ReleaseInfo release, string name, CancellationToken ct)
    {
        var sums = release.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS.txt");
        if (sums is null) return null;
        return ParseSums(await Http.GetStringAsync(sums.Url, ct)).GetValueOrDefault(name);
    }

    /// <summary>sha256sum output: "&lt;hex&gt;  &lt;file&gt;" (binary mode prefixes the name with '*').</summary>
    public static Dictionary<string, string> ParseSums(string text)
    {
        var map = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            map[line[(space + 1)..].TrimStart(' ', '*')] = line[..space].ToLowerInvariant();
        }
        return map;
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    // ---- install --------------------------------------------------------------------------

    /// <summary>
    /// Installs the downloaded package over this copy. On return the new version is in place
    /// (Windows: the installer is running and will relaunch Nexus) and the caller must quit now —
    /// files of the running version may already be replaced. Throws with a user-facing message.
    /// </summary>
    public static async Task InstallAsync(InstallKind kind, string package, CancellationToken ct = default)
    {
        switch (kind)
        {
            case InstallKind.WindowsSetup:
                StartWindowsSetup(package);
                break;
            case InstallKind.MacApp:
                await InstallMacAppAsync(package, ct);
                break;
            case InstallKind.LinuxDeb:
                await RunAsRootAsync(Which("apt-get") is { } apt
                    ? new[] { apt, "install", "-y", "--allow-downgrades", package }
                    : new[] { Which("dpkg") ?? "dpkg", "-i", package }, ct);
                break;
            case InstallKind.LinuxRpm:
                await RunAsRootAsync(Which("dnf") is { } dnf ? new[] { dnf, "install", "-y", package }
                    : Which("zypper") is { } zypper ? new[] { zypper, "--non-interactive", "install", "--allow-unsigned-rpm", package }
                    : new[] { Which("rpm") ?? "rpm", "-U", "--force", package }, ct);
                break;
            case InstallKind.LinuxTarball:
                await InstallTarballAsync(package, ct);
                break;
            default:
                throw new InvalidOperationException("This copy of Nexus wasn't installed by an installer, so it can't update itself.");
        }
    }

    private static void StartWindowsSetup(string setup)
    {
        // Same scope as the existing install, so Inno upgrades it instead of adding a second copy.
        var perUser = AppContext.BaseDirectory.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase);
        // /SILENT shows progress only; /RELAUNCH=1 runs Nexus again when done (see nexus.iss).
        Process.Start(new ProcessStartInfo(setup)
        {
            Arguments = $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1 {(perUser ? "/CURRENTUSER" : "/ALLUSERS")}",
            UseShellExecute = true
        });
    }

    private static async Task InstallMacAppAsync(string dmg, CancellationToken ct)
    {
        var bundle = BundleOf(AppContext.BaseDirectory)
                     ?? throw new InvalidOperationException("Nexus isn't running from an app bundle.");
        var parent = Path.GetDirectoryName(bundle)!;
        var staged = Path.Combine(parent, ".Nexus-update.app");
        var old = Path.Combine(parent, ".Nexus-old.app");
        var mount = Directory.CreateTempSubdirectory("nexus-dmg-").FullName;

        await RunAsync(new[] { "/usr/bin/hdiutil", "attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, dmg }, ct);
        try
        {
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            try
            {
                await RunAsync(new[] { "/usr/bin/ditto", Path.Combine(mount, "Nexus.app"), staged }, ct);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nexus can't write to {parent} ({ex.Message}). Open the downloaded .dmg and drag Nexus to Applications instead.");
            }
        }
        finally
        {
            try { await RunAsync(new[] { "/usr/bin/hdiutil", "detach", mount, "-quiet", "-force" }, CancellationToken.None); }
            catch { /* stays mounted until reboot at worst */ }
        }

        // Swap bundles; the running process keeps its open files. Roll back if the swap fails.
        if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
        Directory.Move(bundle, old);
        try { Directory.Move(staged, bundle); }
        catch { Directory.Move(old, bundle); throw; }
        try { Directory.Delete(old, recursive: true); } catch { /* harmless leftover */ }
        try { await RunAsync(new[] { "/usr/bin/xattr", "-dr", "com.apple.quarantine", bundle }, CancellationToken.None); }
        catch { /* not quarantined */ }
    }

    private static async Task InstallTarballAsync(string tarGz, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("nexus-tar-").FullName;
        try
        {
            await using (var file = File.OpenRead(tarGz))
            await using (var gz = new GZipStream(file, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gz, dir, overwriteFiles: true, ct);
            var script = Directory.GetFiles(dir, "install.sh", SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new InvalidOperationException("The downloaded archive has no install.sh.");
            await RunAsync(new[] { "/bin/sh", script }, ct);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>Linux package managers need root: pkexec shows the desktop's password prompt.</summary>
    private static Task RunAsRootAsync(string[] command, CancellationToken ct)
    {
        if (geteuid() == 0) return RunAsync(command, ct);
        var pkexec = Which("pkexec")
                     ?? throw new InvalidOperationException(
                         $"pkexec (polkit) isn't available to ask for your password. Install the update from a terminal:\n\nsudo {string.Join(' ', command)}");
        return RunAsync(new[] { pkexec }.Concat(command).ToArray(), ct, pkexecDenied: true);
    }

    [DllImport("libc", SetLastError = false)]
    private static extern uint geteuid();

    private static async Task RunAsync(string[] command, CancellationToken ct, bool pkexecDenied = false)
    {
        var psi = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in command.Skip(1)) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {command[0]}.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode == 0) return;
        // pkexec: 126 = the user dismissed the prompt, 127 = not authorized.
        if (pkexecDenied && p.ExitCode is 126 or 127)
            throw new InvalidOperationException("Authorization was cancelled, so the update wasn't installed.");
        var output = ((await stderr).Trim() is { Length: > 0 } e ? e : (await stdout).Trim());
        if (output.Length > 1500) output = "…" + output[^1500..];
        throw new InvalidOperationException($"{Path.GetFileName(command[pkexecDenied ? 1 : 0])} failed (exit {p.ExitCode}):\n{output}");
    }

    private static bool RunQuiet(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string? Which(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin")
            .Split(Path.PathSeparator)
            .Append("/usr/sbin").Append("/sbin")
            .Select(d => Path.Combine(d, name))
            .FirstOrDefault(File.Exists);

    // ---- relaunch -------------------------------------------------------------------------

    /// <summary>
    /// Starts the freshly installed Nexus once this process has exited (the caller quits next).
    /// Windows needs nothing: the installer relaunches it.
    /// </summary>
    public static void RelaunchAfterExit(InstallKind kind)
    {
        if (OperatingSystem.IsWindows()) return;
        string[] start = kind == InstallKind.MacApp && BundleOf(AppContext.BaseDirectory) is { } bundle
            ? new[] { "/usr/bin/open", "-n", bundle }
            : new[] { Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Nexus") };

        // sh -c '<wait for our pid>; exec "$@"' sh <pid> <command…> — detached via setsid when present.
        var psi = new ProcessStartInfo(Which("setsid") ?? "/bin/sh") { UseShellExecute = false };
        if (psi.FileName != "/bin/sh") psi.ArgumentList.Add("/bin/sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("pid=$1; shift; while kill -0 \"$pid\" 2>/dev/null; do sleep 0.2; done; exec \"$@\"");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        foreach (var arg in start) psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }

    /// <summary>Opens a URL or file with the desktop's default handler.</summary>
    public static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
