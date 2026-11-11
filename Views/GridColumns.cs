using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Column building and sizing shared by the results and data-editor grids.
/// <para>
/// Widths are computed here instead of using <c>DataGridLength.Auto</c>: the DataGrid only
/// measures columns that are on screen during its first layout pass, so every column past the
/// right edge stayed at its 20px minimum (unreadable "fal" for booleans, blank headers). We measure
/// the header and a sample of rows in the monospace grid font, cap the width so long text or JSON
/// doesn't produce giant columns (cells trim with "…"; dragging the column shows more), and keep
/// widths the user dragged when the font is zoomed.
/// </para>
/// </summary>
internal static class GridColumns
{
    /// <summary>Automatic widths stop at this many characters; wider values trim with "…".</summary>
    public const int MaxAutoChars = 48;

    /// <summary>"Fit to content" limit (the cell text itself is capped at 1000 characters).</summary>
    public const int MaxFitChars = 300;

    private const int SampleRows = 500;
    private const int MinChars = 4;
    private const double OpenButtonSpace = 24;
    // Header padding (10 + 10) + the template's sort-glyph column (32, reserved even when sorting is
    // off) + separator and rounding.
    private const double HeaderExtra = 56;

    private sealed class GridState
    {
        public double FontSize;
        public bool Hooked;
        public bool FillPending;
        public bool Applying;
    }
    private static readonly ConditionalWeakTable<DataGrid, GridState> States = new();

    /// <summary>
    /// Replaces the grid's columns with <see cref="ValueColumn"/>s for <paramref name="names"/>,
    /// detects JSON/XML columns (engine type, else content), binds <paramref name="rows"/> and sizes.
    /// </summary>
    public static void Build(DataGrid grid, IReadOnlyList<string> names, Func<int, string?> typeOf,
        IList rows, bool editable, Action<ICellSource, ValueColumn>? openValue)
    {
        var state = States.GetOrCreateValue(grid);
        if (!state.Hooked)
        {
            state.Hooked = true;
            grid.SizeChanged += (_, _) => ScheduleFill(grid);
        }

        grid.Columns.Clear();
        grid.ItemsSource = null;

        for (int i = 0; i < names.Count; i++)
        {
            var kind = ValueFormats.DetectColumn(typeOf(i), Sample(rows, i, SampleRows));
            var column = new ValueColumn(i, names[i], kind, editable) { OpenValue = openValue };
            GridCellActions.TrackHeader(grid, column);
            // A user drag changes Width: the last column may have to stop (or start) filling.
            column.PropertyChanged += (_, e) =>
            {
                if (e.Property.Name == nameof(DataGridColumn.Width) && !state.Applying) ScheduleFill(grid);
            };
            grid.Columns.Add(column);
        }

        grid.ItemsSource = rows;
        SizeColumns(grid, keepUserWidths: false);
    }

    /// <summary>
    /// (Re)computes automatic widths. With <paramref name="keepUserWidths"/> (font zoom) columns
    /// the user resized keep their width.
    /// </summary>
    public static void SizeColumns(DataGrid grid, bool keepUserWidths)
    {
        var columns = grid.Columns.OfType<ValueColumn>().ToList();
        if (columns.Count == 0) return;
        var state = States.GetOrCreateValue(grid);
        state.FontSize = UiFonts.Grid;

        var metrics = Metrics.For(grid);
        var rows = grid.ItemsSource as IList;
        foreach (var col in columns)
        {
            if (keepUserWidths && IsUserSized(col)) continue;
            Apply(grid, col, Measure(metrics, col, rows, MaxAutoChars, SampleRows));
        }
        UpdateFill(grid);
    }

    /// <summary>Re-measures after a font size change (called for every grid in the window).</summary>
    public static void RefreshAutoWidths(DataGrid grid)
    {
        if (!grid.Columns.OfType<ValueColumn>().Any()) return;
        SizeColumns(grid, keepUserWidths: true);
    }

    /// <summary>Sizes again if the grid font changed while the grid was off screen (other tab).</summary>
    public static void RefreshIfFontChanged(DataGrid grid)
    {
        if (States.TryGetValue(grid, out var state) && Math.Abs(state.FontSize - UiFonts.Grid) > 0.01)
            RefreshAutoWidths(grid);
    }

    /// <summary>Header menu "Fit to content": widest value (up to <see cref="MaxFitChars"/>) over all rows.</summary>
    public static void FitToContent(DataGrid grid, ValueColumn col)
    {
        Apply(grid, col, Measure(Metrics.For(grid), col, grid.ItemsSource as IList, MaxFitChars, int.MaxValue));
        UpdateFill(grid);
    }

    /// <summary>Header menu "Reset width": back to the automatic width.</summary>
    public static void ResetWidth(DataGrid grid, ValueColumn col)
    {
        Apply(grid, col, Measure(Metrics.For(grid), col, grid.ItemsSource as IList, MaxAutoChars, SampleRows));
        UpdateFill(grid);
    }

    private static bool IsUserSized(ValueColumn col) =>
        !double.IsNaN(col.AssignedWidth) && !col.Width.IsStar
        && Math.Abs(col.Width.Value - col.AssignedWidth) > 0.5;

    private static void Apply(DataGrid grid, ValueColumn col, double width)
    {
        var state = States.GetOrCreateValue(grid);
        width = Math.Ceiling(width);
        state.Applying = true;
        try
        {
            col.AssignedWidth = width;
            col.MinWidth = Math.Min(grid.MinColumnWidth, width);
            col.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel);
        }
        finally { state.Applying = false; }
    }

    private static void ScheduleFill(DataGrid grid)
    {
        var state = States.GetOrCreateValue(grid);
        if (state.FillPending) return;
        state.FillPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            state.FillPending = false;
            UpdateFill(grid);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Lets the last column take the grid's spare width (star, with its own width as minimum) —
    /// otherwise the DataGrid pads rows with an empty "filler" column that has a clickable header
    /// and shows the selection. Only while everything fits, though: with a star column present
    /// and too little room, the DataGrid squeezes every other column down to its 20px minimum to
    /// make the star column fit (that, not Auto sizing, is what made columns unreadable).
    /// </summary>
    private static void UpdateFill(DataGrid grid)
    {
        if (grid.Columns.Count == 0 || grid.Columns[^1] is not ValueColumn last) return;
        var state = States.GetOrCreateValue(grid);
        double baseWidth = last.Width.IsStar ? last.MinWidth : last.Width.Value;
        if (double.IsNaN(baseWidth) || baseWidth <= 0) return;

        double others = 0;
        for (int i = 0; i < grid.Columns.Count - 1; i++)
        {
            var c = grid.Columns[i];
            if (c.IsVisible) others += c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth;
        }

        double available = ViewportWidth(grid);
        bool fits = available > 0 && others + baseWidth <= available;

        state.Applying = true;
        try
        {
            if (fits && !last.Width.IsStar)
            {
                last.MinWidth = baseWidth;
                last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            }
            else if (!fits && last.Width.IsStar)
            {
                last.MinWidth = Math.Min(grid.MinColumnWidth, baseWidth);
                last.Width = new DataGridLength(baseWidth, DataGridLengthUnitType.Pixel);
            }
        }
        finally { state.Applying = false; }
    }

    /// <summary>Width available to the cells: grid minus row-number header and vertical scrollbar.</summary>
    private static double ViewportWidth(DataGrid grid)
    {
        double width = grid.Bounds.Width;
        if (width <= 0) return 0;
        var rowHeader = grid.GetVisualDescendants().OfType<DataGridRowHeader>().FirstOrDefault(h => h.IsVisible);
        width -= rowHeader?.Bounds.Width ?? 48;
        var vbar = grid.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(b => b.Orientation == Orientation.Vertical && b.IsVisible);
        width -= vbar?.Bounds.Width ?? 0;
        return width - 4; // gridlines/rounding slack
    }

    private static double Measure(Metrics m, ValueColumn col, IList? rows, int maxChars, int maxRows)
    {
        int chars = MinChars;
        if (rows is not null)
        {
            int n = Math.Min(rows.Count, maxRows);
            for (int r = 0; r < n && chars < maxChars; r++)
            {
                if (rows[r] is not ICellSource row) continue;
                var text = row.GetText(col.Ordinal) ?? CellText.NullText;
                chars = Math.Max(chars, ValueFormats.DisplayLength(text, maxChars));
            }
        }

        double cell = chars * m.CharWidth + m.CellPadding + (col.Kind != ValueKind.Text ? OpenButtonSpace : 0);
        double header = m.HeaderWidth(col.Header?.ToString() ?? "") + HeaderExtra;
        return Math.Max(cell, header);
    }

    private static IEnumerable<string?> Sample(IList rows, int ordinal, int max)
    {
        int n = Math.Min(rows.Count, max);
        for (int r = 0; r < n; r++)
            if (rows[r] is ICellSource row)
                yield return row.GetText(ordinal);
    }

    /// <summary>Font metrics of the grid's (monospace) font at the current grid font size.</summary>
    private sealed class Metrics
    {
        public double CharWidth { get; private init; }
        public double CellPadding { get; private init; }
        private Typeface _header;
        private double _size;

        public static Metrics For(DataGrid grid)
        {
            var size = UiFonts.Grid;
            var family = grid.FontFamily;
            var probe = new string('0', 20);
            var charWidth = Text(probe, new Typeface(family), size) / probe.Length;

            // Left + right margin of the cell's TextBlock in the DataGrid theme (12 each in Fluent).
            double padding = 24;
            if (grid.TryFindResource("DataGridTextColumnCellTextBlockMargin", out var res) && res is Thickness t)
                padding = t.Left + t.Right;

            return new Metrics
            {
                CharWidth = charWidth,
                CellPadding = padding + 4,
                _header = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold),
                _size = size
            };
        }

        public double HeaderWidth(string header) => Text(header, _header, _size);

        private static double Text(string text, Typeface typeface, double size) =>
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, null).Width;
    }
}
