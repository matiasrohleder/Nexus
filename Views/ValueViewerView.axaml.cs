using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using Nexus.Services;
using Nexus.ViewModels;

namespace Nexus.Views;

public partial class ValueViewerView : UserControl
{
    private ValueViewerViewModel? _vm;
    private bool _syncingText;
    private readonly ValueColorizer _colorizer = new();
    private FoldingManager? _foldings;
    private readonly DispatcherTimer _foldTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public ValueViewerView()
    {
        InitializeComponent();

        Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 2;
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.Parse("#262628"));
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);
        // Fold markers in the editor's dark palette (defaults are for a light background).
        Editor.SetValue(FoldingMargin.FoldingMarkerBrushProperty, new SolidColorBrush(Color.Parse("#808080")));
        Editor.SetValue(FoldingMargin.FoldingMarkerBackgroundBrushProperty, new SolidColorBrush(Color.Parse("#1E1E1E")));
        Editor.SetValue(FoldingMargin.SelectedFoldingMarkerBrushProperty, new SolidColorBrush(Color.Parse("#D4D4D4")));
        Editor.SetValue(FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, new SolidColorBrush(Color.Parse("#2D2D30")));

        Editor.TextChanged += OnEditorTextChanged;
        Editor.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
            UiFonts.ChangeEditor(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        _foldTimer.Tick += (_, _) => { _foldTimer.Stop(); UpdateFoldings(); };
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => _foldings ??= FoldingManager.Install(Editor.TextArea);
        DetachedFromVisualTree += (_, _) =>
        {
            _foldTimer.Stop();
            if (_foldings is null) return;
            FoldingManager.Uninstall(_foldings);
            _foldings = null;
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.CopyToClipboard = null;
        }

        _vm = DataContext as ValueViewerViewModel;
        if (_vm is null) return;

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.CopyToClipboard = async text =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };

        Editor.IsReadOnly = _vm.IsReadOnly;
        Editor.WordWrap = _vm.WordWrap;
        _colorizer.Language = _vm.Kind;
        SetEditorText(_vm.Text);
        Dispatcher.UIThread.Post(UpdateFoldings, DispatcherPriority.Background);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(ValueViewerViewModel.Text) when !_syncingText && Editor.Text != _vm.Text:
                SetEditorText(_vm.Text);
                break;
            case nameof(ValueViewerViewModel.Kind):
                _colorizer.Language = _vm.Kind;
                Editor.TextArea.TextView.Redraw();
                UpdateFoldings();
                break;
            case nameof(ValueViewerViewModel.WordWrap):
                Editor.WordWrap = _vm.WordWrap;
                break;
        }
    }

    private void SetEditorText(string text)
    {
        _syncingText = true;
        Editor.Text = text;
        _syncingText = false;
        _colorizer.Invalidate();
        Editor.TextArea.TextView.Redraw();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        _colorizer.Invalidate();
        Editor.TextArea.TextView.Redraw();
        _foldTimer.Stop();
        _foldTimer.Start();
        if (_syncingText || _vm is null) return;
        _syncingText = true;
        _vm.Text = Editor.Text;
        _syncingText = false;
    }

    private void UpdateFoldings()
    {
        if (_foldings is null || _vm is null || Editor.Document is null) return;
        ValueColorizer.UpdateFoldings(_foldings, Editor.Document, _vm.Kind);
    }
}
