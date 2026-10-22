using System;
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
        ExplorerTree.DoubleTapped += OnExplorerDoubleTapped;
        // Right-click doesn't select a TreeViewItem by default; do it so the context menu
        // targets the node under the cursor (tunnel = runs before the menu opens).
        ExplorerTree.AddHandler(PointerPressedEvent, OnExplorerPointerPressed, RoutingStrategies.Tunnel);
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
        if (!e.GetCurrentPoint(ExplorerTree).Properties.IsRightButtonPressed) return;

        var item = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        if (item is not null)
            item.IsSelected = true;
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

    private Task<bool> ShowOverlayAsync(string message, bool showCancel)
    {
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
