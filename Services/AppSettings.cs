using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Nexus.Services;

/// <summary>Small persisted UI preferences (separate from connections), at ~/.config/Nexus/settings.json.</summary>
public sealed class AppSettings
{
    public const double DefaultEditorFontSize = 14;
    public const double DefaultGridFontSize = 12;

    /// <summary>Process-wide instance: every reader/writer shares it so saves never clobber each other.</summary>
    public static AppSettings Current { get; } = Load();

    public bool SidebarOnLeft { get; set; } = true;

    /// <summary>SQL editor font size (Ctrl+= / Ctrl+- / Ctrl+wheel in the editor).</summary>
    public double EditorFontSize { get; set; } = DefaultEditorFontSize;

    /// <summary>Results / data-editor grid font size.</summary>
    public double GridFontSize { get; set; } = DefaultGridFontSize;

    /// <summary>
    /// Connection folders in display order. Kept here (not in connections.json, which stays a plain
    /// list) so empty folders survive; each connection names its folder in <c>SavedConnection.Folder</c>.
    /// </summary>
    public List<string> Folders { get; set; } = new();

    /// <summary>Folders the user collapsed in the explorer (folders start expanded).</summary>
    public List<string> CollapsedFolders { get; set; } = new();

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
