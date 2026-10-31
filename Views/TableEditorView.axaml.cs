using System;
using Avalonia.Controls;
using Avalonia.Data;
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

        EditGrid.Columns.Clear();
        EditGrid.ItemsSource = null;

        for (int i = 0; i < _vm.ColumnNames.Count; i++)
        {
            EditGrid.Columns.Add(new DataGridTextColumn
            {
                Header = _vm.ColumnNames[i],
                // Two-way reflection binding to EditableRow's string indexer.
                Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay },
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
            });
        }

        EditGrid.ItemsSource = _vm.Rows;
    }
}
