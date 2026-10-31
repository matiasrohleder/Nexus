using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Nexus.Views;

/// <summary>Column sizing shared by the results and data-editor grids.</summary>
internal static class GridColumns
{
    private const string FillClass = "fill-last";

    /// <summary>
    /// Re-measures auto-sized columns after a font size change (they keep their old width
    /// otherwise, clipping text when the font grows).
    /// </summary>
    public static void RefreshAutoWidths(DataGrid grid)
    {
        // Only the result/data grids follow the zoom (the profiler/history grids keep a fixed size).
        var fill = grid.Classes.Contains(FillClass);
        if (!fill) return;
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            var col = grid.Columns[i];
            bool ours = fill && i == grid.Columns.Count - 1;
            if (!col.Width.IsAuto && !ours) continue;
            if (ours) col.MinWidth = 0;
            // Toggling the width forces the grid to measure the column again.
            col.Width = new DataGridLength(1, DataGridLengthUnitType.Pixel);
            col.Width = DataGridLength.Auto;
        }
        if (fill) FillLastColumn(grid);
    }

    /// <summary>
    /// Lets the last column take up the grid's spare width. Otherwise the DataGrid pads the row
    /// with an empty "filler" column that has a clickable header and shows the selection. The
    /// column keeps its auto-sized width as a minimum, so wide results still scroll.
    /// </summary>
    public static void FillLastColumn(DataGrid grid)
    {
        grid.Classes.Add(FillClass);
        var count = grid.Columns.Count;
        if (count == 0) return;
        var last = grid.Columns[count - 1];

        // Auto widths are known after the first layout pass with rows.
        Dispatcher.UIThread.Post(() =>
        {
            if (grid.Columns.Count != count || grid.Columns[count - 1] != last) return; // rebuilt meanwhile
            last.MinWidth = Math.Max(last.MinWidth, last.ActualWidth);
            last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        }, DispatcherPriority.Background);
    }
}
