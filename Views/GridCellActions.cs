using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Nexus.Controls;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Mouse affordances shared by the results and data-editor grids: right-click menus on cells
/// (open value in a tab, copy) and on column headers (fit to content, reset width, copy name),
/// and double-click to open a structured/long value (results grid only — in the data editor a
/// double-click edits the cell).
/// </summary>
internal static class GridCellActions
{
    private sealed class State { public ValueColumn? PressedHeader; }
    private static readonly ConditionalWeakTable<DataGrid, State> States = new();

    /// <param name="open">Opens a cell's value in the value viewer tab.</param>
    /// <param name="openOnDoubleClick">Double-click opens structured, multi-line or long values.</param>
    public static void Attach(DataGrid grid, Action<ICellSource, ValueColumn> open, bool openOnDoubleClick)
    {
        var state = States.GetOrCreateValue(grid);

        // Header presses arrive per column (the header control doesn't expose its column), so
        // remember which one was pressed for the ContextRequested that follows.
        grid.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is null)
                state.PressedHeader = null;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        grid.ContextRequested += (_, e) =>
        {
            var source = e.Source as Visual;
            if (source?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is { } header)
            {
                if (state.PressedHeader is { } column)
                    ShowMenu(header, HeaderMenu(grid, column));
                e.Handled = true;
                return;
            }

            if (Cell(source) is not { } hit) return;
            grid.SelectedItem = hit.Row; // right-click targets the row under the cursor
            ShowMenu(hit.Cell, CellMenu(grid, hit.Row, hit.Column, open));
            e.Handled = true;
        };

        if (openOnDoubleClick)
            grid.DoubleTapped += (_, e) =>
            {
                if (Cell(e.Source as Visual) is not { } hit) return;
                if (!ValueFormats.IsWorthOpening(hit.Row.GetText(hit.Column.Ordinal), hit.Column.Kind)) return;
                open(hit.Row, hit.Column);
                e.Handled = true;
            };

        // A grid in a background tab misses the font-zoom refresh; catch up when it's shown.
        grid.AttachedToVisualTree += (_, _) => GridColumns.RefreshIfFontChanged(grid);
    }

    /// <summary>Called for each new column so header right-clicks know their column.</summary>
    public static void TrackHeader(DataGrid grid, ValueColumn column) =>
        column.HeaderPointerPressed += (_, _) => States.GetOrCreateValue(grid).PressedHeader = column;

    private static (DataGridCell Cell, ICellSource Row, ValueColumn Column)? Cell(Visual? source)
    {
        var cell = source?.FindAncestorOfType<DataGridCell>(includeSelf: true);
        if (cell?.DataContext is not ICellSource row) return null;
        if (DataGridColumn.GetColumnContainingElement(cell) is not ValueColumn column) return null;
        return (cell, row, column);
    }

    private static void ShowMenu(Control target, List<Control> items)
    {
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(target);
    }

    private static List<Control> CellMenu(DataGrid grid, ICellSource row, ValueColumn column, Action<ICellSource, ValueColumn> open)
    {
        var text = row.GetText(column.Ordinal);
        var kind = column.Kind != ValueKind.Text ? column.Kind : ValueFormats.Detect(text);
        return new List<Control>
        {
            Item(kind == ValueKind.Text ? "Open value in new tab" : $"Open {ValueFormats.Label(kind)} in new tab",
                "braces", text is not null, () => open(row, column)),
            Item("Copy value", "copy", text is not null, () => Copy(grid, text)),
            new Separator(),
            Item("Copy column name", "column", true, () => Copy(grid, column.Header?.ToString())),
        };
    }

    private static List<Control> HeaderMenu(DataGrid grid, ValueColumn column) => new()
    {
        Item("Fit to content", "fit-width", true, () => GridColumns.FitToContent(grid, column)),
        Item("Reset width", "undo", true, () => GridColumns.ResetWidth(grid, column)),
        new Separator(),
        Item("Copy column name", "copy", true, () => Copy(grid, column.Header?.ToString())),
    };

    private static MenuItem Item(string header, string icon, bool enabled, Action action)
    {
        var item = new MenuItem { Header = header, Icon = new Icon { Kind = icon, Size = 14 }, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static async void Copy(Control control, string? text)
    {
        if (text is null || TopLevel.GetTopLevel(control)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); }
        catch { /* clipboard unavailable (headless, Wayland without focus) — nothing to report */ }
    }
}
