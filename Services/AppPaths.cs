using System;
using System.IO;

namespace Nexus.Services;

/// <summary>Resolves the per-user configuration directory used by Nexus.</summary>
public static class AppPaths
{
    /// <summary>~/.config/Nexus (or the platform equivalent).</summary>
    public static string ConfigDir { get; }

    public static string ConnectionsFile => Path.Combine(ConfigDir, "connections.json");
    public static string KeyFile => Path.Combine(ConfigDir, "key.bin");

    static AppPaths()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        ConfigDir = Path.Combine(baseDir, "Nexus");
        Directory.CreateDirectory(ConfigDir);
    }
}
