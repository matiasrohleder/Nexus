using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nexus.Controls;
using Nexus.Models;
using Nexus.Providers;
using Nexus.Services;
using Nexus.ViewModels;

namespace Nexus.Views;

public partial class QueryDocumentView : UserControl
{
    private QueryDocumentViewModel? _vm;
    private bool _syncingText;
    private CompletionWindow? _completionWindow;
    private readonly QueryColorizer _colorizer = new();
    private int _completionRequest;

    public QueryDocumentView()
    {
        InitializeComponent();

        // Own tokenizer-based highlighting (see QueryColorizer) instead of the generic SQL definition,
        // whose light-theme colors left almost everything white on the dark editor.
        Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.Parse("#262628"));
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);

        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.KeyDown += OnEditorKeyDown;
        Editor.TextArea.TextEntered += OnTextEntered;
        // Tunnel phase: intercept Ctrl+Enter BEFORE AvaloniaEdit turns Enter into a newline.
        Editor.AddHandler(KeyDownEvent, OnEditorPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Ctrl+wheel zooms the editor; over the results it zooms the grid.
        Editor.AddHandler(PointerWheelChangedEvent, (_, e) => OnZoomWheel(e, UiFonts.ChangeEditor),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ResultsGrid.AddHandler(PointerWheelChangedEvent, (_, e) => OnZoomWheel(e, UiFonts.ChangeGrid),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ResultsGrid.LoadingRow += (_, e) => e.Row.Header = (e.Row.Index + 1).ToString("N0");
        CopyErrorButton.Click += OnCopyError;
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => SchemaCache.Changed += OnSchemaChanged;
        DetachedFromVisualTree += (_, _) => SchemaCache.Changed -= OnSchemaChanged;
    }

    private static void OnZoomWheel(PointerWheelEventArgs e, Action<double> change)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
        change(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    private async void OnCopyError(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null && _vm is not null)
            await clipboard.SetTextAsync(_vm.ErrorText);
    }

    private void OnEditorPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _completionWindow?.Close();
            if (_vm is { IsRunning: false })
                _vm.RunCommand.Execute(null);
            e.Handled = true;
        }
        else if (ZoomDelta(e) is { } delta)
        {
            if (delta == 0) UiFonts.Reset(); else UiFonts.ChangeAll(delta);
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+= / Ctrl++ → +1, Ctrl+- → -1, Ctrl+0 → 0 (reset); null for other keys.</summary>
    internal static int? ZoomDelta(KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return null;
        return e.Key switch
        {
            Key.OemPlus or Key.Add => 1,
            Key.OemMinus or Key.Subtract => -1,
            Key.D0 or Key.NumPad0 => 0,
            _ => null
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as QueryDocumentViewModel;
        if (_vm is null) return;

        // Editor text is not directly bindable, so we sync both directions manually.
        _syncingText = true;
        Editor.Text = _vm.DocumentText;
        _syncingText = false;

        _vm.SelectionProvider = () => Editor.SelectedText;
        _vm.PickSavePath = PickSavePathAsync;
        _vm.PickOpenPath = PickOpenPathAsync;
        _vm.PromptText = PromptTextAsync;
        _vm.PropertyChanged += OnVmPropertyChanged;

        RebuildColumns(_vm.SelectedResult);
        ConfigureLanguage();
    }

    /// <summary>Points highlighting/completion at the tab's connection+database (and warms its schema).</summary>
    private void ConfigureLanguage()
    {
        var conn = _vm?.SchemaConnection;
        _colorizer.Configure(conn is null ? null : ProviderRegistry.For(conn));
        _colorizer.Schema = conn is null ? null : SchemaCache.Get(conn);
        Editor.TextArea.TextView.Redraw();
    }

    private void OnSchemaChanged(Guid connectionId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var conn = _vm?.SchemaConnection;
            if (conn is null || conn.Id != connectionId) return;
            // After Invalidate (refresh/disconnect) don't reload eagerly: that would reconnect.
            // The next completion request loads it again.
            _colorizer.Schema = SchemaCache.TryGet(conn);
            Editor.TextArea.TextView.Redraw();
        });
    }

    // ---- keyboard: completion, zoom, Ctrl+O/Ctrl+S files ---------------------------------

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Space && ctrl)
        {
            _ = ShowCompletionAsync(explicitInvoke: true);
            e.Handled = true;
        }
        else if (e.Key == Key.O && ctrl)
        {
            _vm?.OpenFileCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.S && ctrl)
        {
            _vm?.SaveFileCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Opens completion while typing: on a word's first letter and right after a ".".</summary>
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _vm?.SchemaConnection is null) return;
        var ch = e.Text[^1];

        if (_completionWindow is not null)
        {
            // The list filters itself while the word grows; anything else ends the word.
            if (!SqlLanguage.IsWordPart(ch)) _completionWindow.Close();
            else if (!_completionWindow.CompletionList.CurrentList.Any()) _completionWindow.Close();
            if (ch != '.') return;
        }

        if (ch == '.' || (SqlLanguage.IsWordStart(ch) || ch == '$') && WordLengthBeforeCaret() == 1)
            _ = ShowCompletionAsync(explicitInvoke: false);
    }

    private int WordLengthBeforeCaret()
    {
        int caret = Editor.CaretOffset, start = caret;
        while (start > 0 && SqlLanguage.IsWordPart(Editor.Document.GetCharAt(start - 1))) start--;
        return caret - start;
    }

    private async Task ShowCompletionAsync(bool explicitInvoke)
    {
        var conn = _vm?.SchemaConnection;
        if (conn is null) return;

        var request = ++_completionRequest;
        var text = Editor.Text;
        var caret = Editor.CaretOffset;
        var provider = ProviderRegistry.For(conn);

        CompletionResult? result;
        try { result = await SqlCompletion.CompleteAsync(text, caret, provider, SchemaCache.Get(conn), explicitInvoke); }
        catch { return; }

        // Stale: the user kept typing past the word, moved away, or a newer request started.
        if (request != _completionRequest || result is null) return;
        if (Editor.CaretOffset < result.ReplaceStart || Editor.CaretOffset > Editor.Document.TextLength) return;
        var typed = Editor.Document.GetText(result.ReplaceStart, Editor.CaretOffset - result.ReplaceStart);
        if (typed.Any(ch => !SqlLanguage.IsWordPart(ch))) return;
        if (!explicitInvoke && typed.Length > 0 &&
            !result.Items.Any(i => i.Label.Contains(typed, StringComparison.OrdinalIgnoreCase))) return;

        _completionWindow?.Close();
        var window = new CompletionWindow(Editor.TextArea)
        {
            StartOffset = result.ReplaceStart,
            EndOffset = Editor.CaretOffset,
            CloseWhenCaretAtBeginning = !explicitInvoke && typed.Length > 0,
            MinWidth = 300
        };
        foreach (var item in result.Items)
            window.CompletionList.CompletionData.Add(new CompletionData(item));
        window.Closed += (_, _) => { if (_completionWindow == window) _completionWindow = null; };
        _completionWindow = window;
        window.Show();
        if (typed.Length > 0)
            window.CompletionList.SelectItem(typed);
    }

    /// <summary>A completion row: kind glyph, label, muted detail (column type, "table"…).</summary>
    private sealed class CompletionData : ICompletionData
    {
        private readonly CompletionItem _item;
        private object? _content;

        public CompletionData(CompletionItem item) => _item = item;

        public string Text => _item.Label;
        public object Description => _item.Detail ?? _item.Kind.ToString().ToLowerInvariant();
        public Avalonia.Media.IImage? Image => null;
        public double Priority => -_item.Rank;

        public object Content => _content ??= BuildContent();

        private Control BuildContent()
        {
            var (glyph, color) = _item.Kind switch
            {
                CompletionKind.Table => ("table", "#4EC9B0"),
                CompletionKind.View => ("view", "#C4B5FD"),
                CompletionKind.Schema => ("folder", "#FBBF24"),
                CompletionKind.Column => ("column", "#9CDCFE"),
                CompletionKind.Alias => ("link", "#4EC9B0"),
                CompletionKind.Function or CompletionKind.Method => ("function", "#DCDCAA"),
                CompletionKind.Operator => ("zap", "#C586C0"),
                _ => ("keyword", "#569CD6")
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new Icon
            {
                Kind = glyph, Size = 13, StrokeThickness = 1.8, Margin = new Thickness(0, 0, 8, 0),
                Foreground = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = VerticalAlignment.Center
            });
            var label = new TextBlock { Text = _item.Label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            if (_item.Detail is { Length: > 0 } detail)
            {
                var d = new TextBlock
                {
                    Text = detail, Opacity = 0.5, FontSize = 11, Margin = new Thickness(16, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(d, 2);
                row.Children.Add(d);
            }
            return row;
        }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            textArea.Document.Replace(completionSegment, _item.InsertText);
            if (_item.CaretBack > 0)
                textArea.Caret.Offset = Math.Max(0, textArea.Caret.Offset - _item.CaretBack);
        }
    }

    // ---- pickers / prompts ----------------------------------------------------------------

    private async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save file",
            SuggestedFileName = suggestedName
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickOpenPathAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SQL file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SQL files") { Patterns = new[] { "*.sql" } },
                FilePickerFileTypes.All
            }
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PromptTextAsync(string label)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return null;

        var input = new TextBox { PlaceholderText = label };
        var ok = new Button { Content = "Save", MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        var dialog = new Window
        {
            Title = label,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Avalonia.Media.Brush.Parse("#1E1E1E"),
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = label },
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancel, ok }
                    }
                }
            }
        };
        ok.Click += (_, _) => dialog.Close(input.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        input.KeyDown += (_, args) => { if (args.Key == Key.Enter) dialog.Close(input.Text); };

        return await dialog.ShowDialog<string?>(owner);
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        // Multi-line constructs (/* */, strings) can change the look of lines below the edit.
        _colorizer.Invalidate();
        Editor.TextArea.TextView.Redraw();
        if (_syncingText || _vm is null) return;
        _vm.DocumentText = Editor.Text;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryDocumentViewModel.SelectedResult))
            RebuildColumns(_vm?.SelectedResult);
        else if (e.PropertyName == nameof(QueryDocumentViewModel.SchemaConnection))
            ConfigureLanguage();
        else if (e.PropertyName == nameof(QueryDocumentViewModel.DocumentText) && _vm is not null
                 && !_syncingText && Editor.Text != _vm.DocumentText)
        {
            _syncingText = true;
            Editor.Text = _vm.DocumentText;
            _syncingText = false;
        }
    }

    /// <summary>Rebuilds the results DataGrid columns from the runtime schema of the result set.</summary>
    private void RebuildColumns(QueryResult? result)
    {
        ResultsGrid.Columns.Clear();
        ResultsGrid.ItemsSource = null;

        if (result is null || !result.HasResultSet)
            return;

        for (int i = 0; i < result.Columns.Count; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i],
                // Reflection binding to ResultRow's string indexer (schema known only at runtime).
                Binding = new Binding($"[{i}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
            });
        }

        ResultsGrid.ItemsSource = result.Rows;
    }
}
