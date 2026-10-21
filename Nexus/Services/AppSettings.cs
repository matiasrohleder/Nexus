using System.IO;
using System.Text.Json;

namespace Nexus.Services;

/// <summary>Small persisted UI preferences (separate from connections), at ~/.config/Nexus/settings.json.</summary>
public sealed class AppSettings
{
    public bool SidebarOnLeft { get; set; } = true;

    private static string FilePath => Path.Combine(AppPaths.ConfigDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* best effort */ }
    }
}
