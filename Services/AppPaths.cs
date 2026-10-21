using System;
using System.IO;

namespace Nexus.Services;

/// <summary>Resolves the per-user directories used by Nexus.</summary>
public static class AppPaths
{
    /// <summary>
    /// Portable, user-visible settings (connections.json, settings, history, snippets):
    /// ~/.config/Nexus on Linux, ~/Library/Application Support/Nexus on macOS, %APPDATA%\Nexus on Windows.
    /// Never contains secrets, so connections.json can be copied to another machine as-is.
    /// </summary>
    public static string ConfigDir { get; private set; }

    /// <summary>
    /// Machine-local data that must not travel with the config (the fallback secret vault):
    /// ~/.local/share/Nexus on Linux, ~/Library/Application Support/Nexus on macOS,
    /// %LOCALAPPDATA%\Nexus on Windows.
    /// </summary>
    public static string DataDir { get; private set; }

    public static string ConnectionsFile => Path.Combine(ConfigDir, "connections.json");

    /// <summary>Pre-0.2 AES key that encrypted passwords inside connections.json (migration only).</summary>
    public static string LegacyKeyFile => Path.Combine(ConfigDir, "key.bin");

    static AppPaths()
    {
        // DoNotVerify: the default returns "" when the folder doesn't exist yet (fresh accounts,
        // custom XDG_* dirs), which would silently put everything in the wrong place.
        const Environment.SpecialFolderOption dnv = Environment.SpecialFolderOption.DoNotVerify;

        if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, dnv),
                "Library", "Application Support", "Nexus");
            ConfigDir = support;
            DataDir = support;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, dnv);
            var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, dnv);
            if (string.IsNullOrEmpty(config))
                config = Path.Combine(home, ".config");
            var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, dnv);
            if (string.IsNullOrEmpty(data))
                data = Path.Combine(home, ".local", "share");

            ConfigDir = Path.Combine(config, "Nexus");
            DataDir = Path.Combine(data, "Nexus");
        }

        Directory.CreateDirectory(ConfigDir);
    }

    /// <summary>
    /// Self-tests: point every path at a throwaway directory so a test run can never read,
    /// rewrite or delete the user's real connections, settings or legacy key.
    /// </summary>
    public static void RedirectForTests(string root)
    {
        ConfigDir = Path.Combine(root, "config");
        DataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(ConfigDir);
    }
}
