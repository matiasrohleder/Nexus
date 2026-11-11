using System;
using Avalonia;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Nexus.Controls;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Text column for the results and data-editor grids. Cells show a one-line, length-capped
/// form of the value (line breaks as "↵", "…" when it doesn't fit) so a JSON document or a long
/// text can't blow a row up; editing still binds the full value. JSON/XML columns get a small
/// button that opens the value formatted in its own tab.
/// </summary>
internal sealed class ValueColumn : DataGridTextColumn
{
    public ValueColumn(int ordinal, string header, ValueKind kind, bool editable)
    {
        Ordinal = ordinal;
        Kind = kind;
        Header = header;
        // Reflection binding to the row's string indexer (schema known only at runtime).
        Binding = new Binding($"[{ordinal}]") { Mode = editable ? BindingMode.TwoWay : BindingMode.OneWay };
    }

    /// <summary>Index of the value in the row (not the display index, which can change).</summary>
    public int Ordinal { get; }

    /// <summary>Structured kind of the whole column (Text = plain, no open button).</summary>
    public ValueKind Kind { get; }

    /// <summary>Width Nexus last assigned; a different width means the user resized the column.</summary>
    public double AssignedWidth { get; set; } = double.NaN;

    /// <summary>Invoked by the cell's open button with the row.</summary>
    public Action<ICellSource, ValueColumn>? OpenValue { get; set; }

    protected override Control GenerateElement(DataGridCell cell, object dataItem)
    {
        var element = base.GenerateElement(cell, dataItem);
        if (element is not TextBlock text) return element;

        var path = $"[{Ordinal}]";
        text.Bind(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay, Converter = DisplayConverter.Instance });
        text.Bind(TextBlock.OpacityProperty, new Binding(path) { Mode = BindingMode.OneWay, Converter = NullOpacityConverter.Instance });
        text.Bind(ToolTip.TipProperty, new Binding(path) { Mode = BindingMode.OneWay, Converter = TooltipConverter.Instance });
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.TextWrapping = TextWrapping.NoWrap;
        ToolTip.SetShowDelay(text, 700);
        if (Kind == ValueKind.Text) return text;

        var open = new Button
        {
            Classes = { "cell-open" },
            Content = new Icon { Kind = "braces", Size = 12 },
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false
        };
        ToolTip.SetTip(open, $"Open {ValueFormats.Label(Kind)} in a new tab");
        open.Bind(Visual.IsVisibleProperty, new Binding(path) { Mode = BindingMode.OneWay, Converter = NotNullConverter.Instance });
        open.Click += (_, e) =>
        {
            if (open.DataContext is ICellSource row) OpenValue?.Invoke(row, this);
            e.Handled = true;
        };

        DockPanel.SetDock(open, Dock.Right);
        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(open);
        panel.Children.Add(text);
        return panel;
    }

    private static bool IsNull(object? value) => value is null or CellText.NullText;

    private sealed class DisplayConverter : IValueConverter
    {
        public static readonly DisplayConverter Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            ValueFormats.ToDisplay(value as string);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>"(null)" renders dimmed so it reads as "no value", not as the text "(null)".</summary>
    private sealed class NullOpacityConverter : IValueConverter
    {
        public static readonly NullOpacityConverter Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            IsNull(value) ? 0.45 : 1.0;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class TooltipConverter : IValueConverter
    {
        public static readonly TooltipConverter Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            ValueFormats.Tooltip(value as string) is { } tip
                ? new TextBlock
                {
                    Text = tip,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 640,
                    MaxHeight = 400,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
                : null;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class NotNullConverter : IValueConverter
    {
        public static readonly NotNullConverter Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !IsNull(value);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
