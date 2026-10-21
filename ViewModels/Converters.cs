using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Nexus.ViewModels;

/// <summary>true → green, false → red. Used to color the connection-test result message.</summary>
public sealed class BoolBrushes : IValueConverter
{
    public static readonly BoolBrushes Instance = new();

    private static readonly IBrush Ok = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x77));
    private static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Ok : Bad;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Colors a profiler row by success (green) / error (red).</summary>
public sealed class StatusBrushes : IValueConverter
{
    public static readonly StatusBrushes Instance = new();

    private static readonly IBrush Ok = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x77));
    private static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Ok : Bad;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
