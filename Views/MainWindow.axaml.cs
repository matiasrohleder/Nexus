using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Nexus.ViewModels;

namespace Nexus.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // handledEventsToo: TreeViewItem marks double-taps handled after toggling expansion.
        ExplorerTree.AddHandler(DoubleTappedEvent, OnExplorerDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        // Right-click doesn't select a TreeViewItem by default; do it so the context menu
        // targets the node under the cursor (tunnel = runs before the menu opens).
        ExplorerTree.AddHandler(PointerPressedEvent, OnExplorerPointerPressed, RoutingStrategies.Tunnel);
        ExplorerMenu.Opening += OnExplorerMenuOpening;

        // Drag and drop to reorder connections/folders and move connections into folders.
        ExplorerTree.AddHandler(PointerMovedEvent, OnExplorerPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        ExplorerTree.AddHandler(PointerReleasedEvent, (_, _) => _dragCandidate = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(ExplorerTree, true);
        ExplorerTree.AddHandler(DragDrop.DragOverEvent, OnExplorerDragOver);
        ExplorerTree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHint(null, DropPosition.None));
        ExplorerTree.AddHandler(DragDrop.DropEvent, OnExplorerDrop);
        OverlayInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !OverlayInput.IsDropDownOpen)
            {
                ResolveOverlay(true);
                e.Handled = true;
            }
        };
        HistoryGrid.DoubleTapped += (_, _) =>
            _vm?.OpenFromHistoryCommand.Execute(HistoryGrid.SelectedItem);
        SnippetsGrid.DoubleTapped += (_, _) =>
            _vm?.OpenSnippetCommand.Execute(SnippetsGrid.SelectedItem);
        OverlayOkButton.Click += (_, _) => ResolveOverlay(true);
        OverlayCancelButton.Click += (_, _) => ResolveOverlay(false);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && ConfirmOverlay.IsVisible)
            {
                ResolveOverlay(false);
                e.Handled = true;
            }
        };
    }

    private void OnExplorerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(ExplorerTree).Properties;
        if (props.IsLeftButtonPressed)
        {
            // Remember a possible drag start; the drag begins once the pointer travels a few pixels.
            _dragCandidate = NodeRowAt(e.Source)?.DataContext is TreeNodeViewModel { CanMove: true } n ? n : null;
            _dragPress = e;
            _dragOrigin = e.GetPosition(ExplorerTree);
            return;
        }
        if (!props.IsRightButtonPressed) return;

        // Set the TreeView's SelectedItem itself: TreeViewItem.IsSelected no longer propagates to it
        // (Avalonia 12), which left the menu acting on the previously selected node. Right-click on
        // empty space clears the selection so the menu doesn't open for a stale node.
        var item = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        ExplorerTree.SelectedItem = item?.DataContext;
    }

    // ---- explorer drag and drop ---------------------------------------------------------

    private static readonly DataFormat<string> NodeFormat = DataFormat.CreateStringApplicationFormat("nexus-explorer-node");
    private TreeNodeViewModel? _dragCandidate, _dragging, _dropTarget;
    private PointerPressedEventArgs? _dragPress;
    private Point _dragOrigin;

    /// <summary>The row panel of the explorer item under a visual (our item template's "node-row").</summary>
    private Control? NodeRowAt(object? source)
    {
        var visual = source as Visual;
        var row = visual?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("node-row"));
        if (row is not null) return row;
        // Pointer over the item's padding/expander: use that item's own row (its first node-row descendant).
        var item = visual?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        return item?.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("node-row"));
    }

    private async void OnExplorerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is null || _dragPress is null || _dragging is not null) return;
        if (!e.GetCurrentPoint(ExplorerTree).Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            return;
        }
        var delta = e.GetPosition(ExplorerTree) - _dragOrigin;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 6) return;

        _dragging = _dragCandidate;
        _dragCandidate = null;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(NodeFormat, _dragging.Name));
            await DragDrop.DoDragDropAsync(_dragPress, data, DragDropEffects.Move);
        }
        catch { /* platform without drag support: the context menu still offers Move Up/Down/To Folder */ }
        finally
        {
            SetDropHint(null, DropPosition.None);
            _dragging = null;
            _dragPress = null;
        }
    }

    /// <summary>Target node and position for the pointer: upper/lower half of a row = before/after.</summary>
    private (TreeNodeViewModel? target, DropPosition position) DropTargetAt(DragEventArgs e)
    {
        var row = NodeRowAt(e.Source);
        if (row?.DataContext is not TreeNodeViewModel target) return (null, DropPosition.After);
        var y = e.GetPosition(row).Y;
        var h = Math.Max(1, row.Bounds.Height);
        var wanted = y < h / 2 ? DropPosition.Before : DropPosition.After;
        // Dropping onto a connection inside a folder, or any child row, resolves to its connection root.
        while (target is { CanMove: false, Parent: not null }) target = target.Parent;
        return target.CanMove ? (target, wanted) : (null, DropPosition.None);
    }

    private void OnExplorerDragOver(object? sender, DragEventArgs e)
    {
        if (_dragging is null || _vm is null || !e.DataTransfer.Contains(NodeFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        var (target, wanted) = DropTargetAt(e);
        var position = wanted == DropPosition.None ? DropPosition.None : _vm.ResolveDrop(_dragging, target, wanted);
        e.DragEffects = position == DropPosition.None ? DragDropEffects.None : DragDropEffects.Move;
        SetDropHint(target, position);
        e.Handled = true;
    }

    private void OnExplorerDrop(object? sender, DragEventArgs e)
    {
        if (_dragging is null || _vm is null) return;
        var (target, wanted) = DropTargetAt(e);
        SetDropHint(null, DropPosition.None);
        if (wanted != DropPosition.None)
            _vm.MoveNode(_dragging, target, wanted);
        e.Handled = true;
    }

    private void SetDropHint(TreeNodeViewModel? target, DropPosition position)
    {
        if (_dropTarget is not null && _dropTarget != target) _dropTarget.DropHint = DropPosition.None;
        _dropTarget = target;
        if (target is not null) target.DropHint = position;
    }

    private void OnExplorerMenuOpening(object? sender, CancelEventArgs e)
    {
        if (_vm?.SelectedNode is not { HasContextMenu: true })
            e.Cancel = true;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as MainWindowViewModel;
        if (_vm is null) return;

        _vm.ShowConnectionEditor = ShowConnectionEditorAsync;
        _vm.Confirm = ShowConfirmAsync;
        _vm.PickCsvFile = PickCsvFileAsync;
        _vm.PickExportPath = PickExportPathAsync;
        _vm.PickImportFile = PickConnectionsFileAsync;
        _vm.Notify = message => _ = ShowNoticeAsync(message);
        _vm.Prompt = ShowPromptAsync;
        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplySidebarSide(_vm.SidebarOnLeft);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SidebarOnLeft) && _vm is not null)
            ApplySidebarSide(_vm.SidebarOnLeft);
    }

    /// <summary>Places the sidebar (fixed-width column) and content (star column) on the chosen side.</summary>
    private void ApplySidebarSide(bool onLeft)
    {
        var sidebarCol = onLeft ? 0 : 2;
        var contentCol = onLeft ? 2 : 0;

        Grid.SetColumn(SidebarPanel, sidebarCol);
        Grid.SetColumn(ContentPanel, contentCol);

        var cols = MainSplitGrid.ColumnDefinitions;
        var sidebarWidth = SidebarPanel.Bounds.Width > 0 ? SidebarPanel.Bounds.Width : 280;

        // The sidebar's column is the fixed-width one; the content's column takes the remaining space.
        cols[0].Width = onLeft ? new GridLength(sidebarWidth) : new GridLength(1, GridUnitType.Star);
        cols[2].Width = onLeft ? new GridLength(1, GridUnitType.Star) : new GridLength(sidebarWidth);

        cols[0].MinWidth = onLeft ? 180 : 0;
        cols[2].MinWidth = onLeft ? 0 : 180;
    }

    private async Task<bool> ShowConnectionEditorAsync(ConnectionEditorViewModel editorVm)
    {
        var dialog = new ConnectionEditorWindow { DataContext = editorVm };
        return await dialog.ShowDialog<bool>(this);
    }

    private void OnExplorerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedNode is { IsQueryable: true } node)
            vm.OpenTableCommand.Execute(node);
    }

    // ---- in-window confirm/notice overlay (no OS dialogs: Wayland-safe) -----------------

    private TaskCompletionSource<bool>? _confirmTcs;

    private Task<bool> ShowConfirmAsync(string message) => ShowOverlayAsync(message, showCancel: true);

    private Task ShowNoticeAsync(string message) => ShowOverlayAsync(message, showCancel: false);

    /// <summary>In-window text prompt (same overlay, with an input). Returns null when cancelled.</summary>
    private async Task<string?> ShowPromptAsync(string message, string initial, IReadOnlyList<string> suggestions)
    {
        OverlayInput.ItemsSource = suggestions;
        OverlayInput.Text = initial;
        OverlayInput.IsVisible = true;
        var task = ShowOverlayAsync(message, showCancel: true);
        OverlayIcon.Kind = "edit";
        OverlayIcon.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#60A5FA"));
        OverlayOkButton.Content = "OK";
        OverlayInput.Focus();
        var ok = await task;
        OverlayInput.IsVisible = false;
        return ok ? OverlayInput.Text ?? "" : null;
    }

    private Task<bool> ShowOverlayAsync(string message, bool showCancel)
    {
        OverlayInput.IsVisible = OverlayInput.IsVisible && showCancel;
        // If a previous overlay is somehow still pending, resolve it as cancelled.
        _confirmTcs?.TrySetResult(false);
        _confirmTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        ConfirmText.Text = message;
        OverlayIcon.Kind = showCancel ? "warning" : "info";
        OverlayIcon.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(showCancel ? "#FBBF24" : "#60A5FA"));
        OverlayCancelButton.IsVisible = showCancel;
        OverlayOkButton.Content = showCancel ? "Confirm" : "OK";
        ConfirmOverlay.IsVisible = true;
        OverlayOkButton.Focus();
        return _confirmTcs.Task;
    }

    private void ResolveOverlay(bool result)
    {
        ConfirmOverlay.IsVisible = false;
        _confirmTcs?.TrySetResult(result);
        _confirmTcs = null;
    }

    private async Task<string?> PickCsvFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import CSV",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } },
                Avalonia.Platform.Storage.FilePickerFileTypes.All
            }
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static readonly FilePickerFileType JsonFiles = new("Nexus connections (JSON)") { Patterns = new[] { "*.json" } };

    private async Task<string?> PickExportPathAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export connections",
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = new[] { JsonFiles }
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickConnectionsFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import connections",
            AllowMultiple = false,
            FileTypeFilter = new[] { JsonFiles, FilePickerFileTypes.All }
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
