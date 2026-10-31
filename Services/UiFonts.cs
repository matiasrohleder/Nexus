using System;
using Avalonia;

namespace Nexus.Services;

/// <summary>
/// User-adjustable font sizes. Published as the app resources <c>EditorFontSize</c> and
/// <c>GridFontSize</c> (views bind with DynamicResource, so every open tab updates live) and
/// persisted in <see cref="AppSettings"/>.
/// </summary>
public static class UiFonts
{
    public const double Min = 8, Max = 36;

    public static double Editor => AppSettings.Current.EditorFontSize;
    public static double Grid => AppSettings.Current.GridFontSize;

    /// <summary>Pushes the saved sizes into the application resources (call once at startup).</summary>
    public static void Apply()
    {
        if (Application.Current is not { } app) return;
        app.Resources["EditorFontSize"] = Clamp(AppSettings.Current.EditorFontSize);
        app.Resources["GridFontSize"] = Clamp(AppSettings.Current.GridFontSize);
    }

    public static void ChangeEditor(double delta) => Set(Editor + delta, Grid);
    public static void ChangeGrid(double delta) => Set(Editor, Grid + delta);

    /// <summary>Zooms editor and grids together (Ctrl+= / Ctrl+-).</summary>
    public static void ChangeAll(double delta) => Set(Editor + delta, Grid + delta);

    public static void Reset() => Set(AppSettings.DefaultEditorFontSize, AppSettings.DefaultGridFontSize);

    private static void Set(double editor, double grid)
    {
        AppSettings.Current.EditorFontSize = Clamp(editor);
        AppSettings.Current.GridFontSize = Clamp(grid);
        AppSettings.Current.Save();
        Apply();
    }

    private static double Clamp(double size) => Math.Clamp(Math.Round(size), Min, Max);
}
