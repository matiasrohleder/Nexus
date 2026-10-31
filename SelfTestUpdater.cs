using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nexus.Services;

namespace Nexus;

/// <summary>
/// Verifies the self-updater without installing anything by default:
///   dotnet run -- --selftest updater
/// Version ordering, release parsing, asset selection, checksum handling, how this copy was
/// installed (NEXUS_EXPECT_INSTALL=windows|macos|deb|rpm|tarball|manual makes that a check) and a
/// live read of the latest GitHub release (reported, never fatal: CI runners get rate-limited).
/// NEXUS_UPDATE_APPLY=&lt;package&gt; additionally installs that package over this copy with the
/// same code path the update dialog uses (CI re-applies the freshly built installer);
/// NEXUS_UPDATE_APPLY=latest downloads the feed's asset first (pair with NEXUS_UPDATE_FEED).
/// </summary>
public static class SelfTestUpdater
{
    private static int _failures;

    private const string SampleRelease = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/matiasrohleder/Nexus/releases/tag/v1.2.0",
          "body": "Download the installer for your system below — see the README for step-by-step instructions.\n\n## What's Changed\n* Faster grids by **someone**\n",
          "assets": [
            { "name": "Nexus-1.2.0-linux-x64.deb", "size": 10, "digest": "sha256:ABCDEF",
              "browser_download_url": "https://example.invalid/Nexus-1.2.0-linux-x64.deb" },
            { "name": "Nexus-1.2.0-windows-x64-setup.exe", "size": 20,
              "browser_download_url": "https://example.invalid/Nexus-1.2.0-windows-x64-setup.exe" }
          ]
        }
        """;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("== Nexus self-test: updater ==");
        Console.WriteLine($"   version {AppInfo.Version}, install: {Updater.Kind} ({Updater.Describe(Updater.Kind)}), " +
                          $"arch {RuntimeInformation.ProcessArchitecture}");

        // ---- 1. Version ordering ----
        Check(Updater.IsNewer("0.4.0", "0.3.0"), "0.4.0 is newer than 0.3.0");
        Check(Updater.IsNewer("0.10.0", "0.9.0"), "numeric, not lexical (0.10.0 > 0.9.0)");
        Check(Updater.IsNewer("v1.0.0", "0.9.9"), "'v' tag prefix ignored");
        Check(!Updater.IsNewer("0.3.0", "0.3.0"), "same version is not an update");
        Check(!Updater.IsNewer("0.2.0", "0.3.0"), "older release is not an update");
        Check(Updater.IsNewer("1.0.0", "1.0.0-beta.2"), "release beats its pre-release");
        Check(!Updater.IsNewer("1.0.0-beta", "1.0.0"), "pre-release is not newer than the release");

        // ---- 2. Release parsing + asset selection ----
        var release = Updater.Parse(SampleRelease);
        Check(release.Version == "1.2.0", "tag v1.2.0 parses to version 1.2.0");
        Check(release.Assets.Count == 2 && release.Assets[0].Sha256 == "abcdef" && release.Assets[1].Sha256 is null,
            "assets parsed with optional sha256 digests");
        Check(!release.Notes.Contains("Download the installer") && release.Notes.Contains("• Faster grids by someone"),
            "release notes cleaned for plain-text display");
        Check(Updater.AssetNameFor(InstallKind.LinuxDeb, "1.2.0", Architecture.X64) == "Nexus-1.2.0-linux-x64.deb" &&
              Updater.AssetNameFor(InstallKind.LinuxRpm, "1.2.0", Architecture.X64) == "Nexus-1.2.0-linux-x64.rpm" &&
              Updater.AssetNameFor(InstallKind.LinuxTarball, "1.2.0", Architecture.Arm64) == "Nexus-1.2.0-linux-arm64.tar.gz" &&
              Updater.AssetNameFor(InstallKind.MacApp, "1.2.0", Architecture.Arm64) == "Nexus-1.2.0-macos-arm64.dmg" &&
              Updater.AssetNameFor(InstallKind.WindowsSetup, "1.2.0", Architecture.X64) == "Nexus-1.2.0-windows-x64-setup.exe" &&
              Updater.AssetNameFor(InstallKind.Manual, "1.2.0", Architecture.X64) is null,
            "asset names match the packaging scripts");
        Check(Updater.BundleOf("/Applications/Nexus.app/Contents/MacOS/") == "/Applications/Nexus.app" &&
              Updater.BundleOf("/opt/nexus/") is null,
            "macOS bundle path resolved from the executable folder");

        // ---- 3. Checksums ----
        var sums = Updater.ParseSums("ABC123  Nexus-1.2.0-linux-x64.deb\nfff *SHA-binary.exe\n");
        Check(sums.GetValueOrDefault("Nexus-1.2.0-linux-x64.deb") == "abc123" && sums.GetValueOrDefault("SHA-binary.exe") == "fff",
            "SHA256SUMS.txt parsed (text and binary mode)");
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, "abc");
            Check(await Updater.Sha256Async(tmp) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                "SHA-256 of a file");
        }
        finally { File.Delete(tmp); }

        // ---- 4. How this copy was installed ----
        if (Environment.GetEnvironmentVariable("NEXUS_EXPECT_INSTALL") is { Length: > 0 } expect)
        {
            var expected = expect switch
            {
                "windows" => InstallKind.WindowsSetup,
                "macos" => InstallKind.MacApp,
                "deb" => InstallKind.LinuxDeb,
                "rpm" => InstallKind.LinuxRpm,
                "tarball" => InstallKind.LinuxTarball,
                _ => InstallKind.Manual
            };
            Check(Updater.Kind == expected, $"install detected as {expected}");
        }

        // ---- 5. Live release feed (informational) ----
        try
        {
            var latest = await Updater.GetLatestAsync();
            var asset = Updater.AssetFor(latest);
            Console.WriteLine($"   [INFO] latest release {latest.Version} ({latest.Assets.Count} assets); " +
                              $"update for this copy: {asset?.Name ?? "manual download"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   [INFO] latest release not reachable: {ex.Message}");
        }

        // ---- 6. Optional: install a package over this copy ----
        if (Environment.GetEnvironmentVariable("NEXUS_UPDATE_APPLY") is { Length: > 0 } package)
        {
            try
            {
                // "latest" = the whole dialog flow: feed → asset → download + checksum → install.
                if (package == "latest")
                {
                    var latest = await Updater.GetLatestAsync();
                    var asset = Updater.AssetFor(latest) ?? throw new InvalidOperationException("no asset for this copy");
                    package = await Updater.DownloadAsync(latest, asset, null, default);
                    Check(await Updater.Sha256Async(package) == asset.Sha256 || asset.Sha256 is null, $"downloaded and verified {asset.Name}");
                }
                await Updater.InstallAsync(Updater.Kind, Path.GetFullPath(package));
                Check(File.Exists(Environment.ProcessPath), $"installed {Path.GetFileName(package)} over this copy");
            }
            catch (Exception ex)
            {
                Check(false, $"installing {Path.GetFileName(package)}: {ex.Message}");
            }
        }

        Console.WriteLine(_failures == 0 ? "\n✅ ALL CHECKS PASSED" : $"\n❌ {_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string label)
    {
        Console.WriteLine($"   [{(condition ? "PASS" : "FAIL")}] {label}");
        if (!condition) _failures++;
    }
}
