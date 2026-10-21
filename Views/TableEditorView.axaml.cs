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
