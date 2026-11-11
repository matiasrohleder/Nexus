using System;
using Avalonia.Controls;
using Nexus.Models;
using Nexus.ViewModels;

namespace Nexus.Views;

public partial class TableEditorView : UserControl
{
    private TableEditorViewModel? _vm;

    public TableEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        EditGrid.CellEditEnded += (_, _) => _vm?.UpdatePendingStatus();
        EditGrid.LoadingRow += (_, e) => e.Row.Header = (e.Row.Index + 1).ToString("N0");
        // Double-click edits here, so values open from the cell button or the right-click menu.
        GridCellActions.Attach(EditGrid, OpenValue, openOnDoubleClick: false);
        // Ctrl+wheel zooms the grid font (shared with the query results grids).
        EditGrid.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) || e.Delta.Y == 0) return;
            Services.UiFonts.ChangeGrid(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.SchemaLoaded -= OnSchemaLoaded;

        _vm = DataContext as TableEditorViewModel;
        if (_vm is null) return;

        _vm.SchemaLoaded += OnSchemaLoaded;
        RebuildColumns();
    }

    private void OnSchemaLoaded(object? sender, EventArgs e) => RebuildColumns();

    private void RebuildColumns()
    {
        if (_vm is null) return;
        var vm = _vm;
        GridColumns.Build(EditGrid, vm.ColumnNames, vm.ColumnType, vm.Rows, editable: true, OpenValue);
    }

    /// <summary>
    /// Opens a cell in a value tab. On an editable table the tab can write the value back into the
    /// cell (pending until Commit, like a grid edit).
    /// </summary>
    private void OpenValue(ICellSource source, ValueColumn column)
    {
        if (_vm is not { } vm || source is not EditableRow row || row.GetCurrent(column.Ordinal) is not { } text) return;
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel main) return;

        Func<string, string?>? apply = vm.IsReadOnly ? null : value =>
        {
            if (!vm.Rows.Contains(row))
                return "That row is no longer in the data editor (it was refreshed or deleted).";
            row[column.Ordinal] = value;
            vm.UpdatePendingStatus();
            return null;
        };
        var header = column.Header?.ToString() ?? "value";
        main.OpenValueViewer(new ValueViewerViewModel($"{header} · row {vm.Rows.IndexOf(row) + 1:N0}", header,
            vm.ColumnType(column.Ordinal), text, column.Kind, (row, column.Ordinal), apply));
    }
}
