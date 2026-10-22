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
using AvaloniaEdit.Highlighting;
using Nexus.Models;
using Nexus.Services;
using Nexus.ViewModels;

namespace Nexus.Views;

public partial class QueryDocumentView : UserControl
{
    private QueryDocumentViewModel? _vm;
    private bool _syncingText;
    private CompletionWindow? _completionWindow;

    public QueryDocumentView()
    {
        InitializeComponent();

        // Built-in SQL syntax highlighting (ships with AvaloniaEdit).
        try
        {
            Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("SQL");
        }
        catch { /* highlighting is optional */ }

        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.KeyDown += OnEditorKeyDown;
        // Tunnel phase: intercept Ctrl+Enter BEFORE AvaloniaEdit turns Enter into a newline.
        Editor.AddHandler(KeyDownEvent, OnEditorPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        CopyErrorButton.Click += OnCopyError;
        DataContextChanged += OnDataContextChanged;
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
            if (_vm is { IsRunning: false })
                _vm.RunCommand.Execute(null);
            e.Handled = true;
        }
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

        // Warm the schema cache for autocomplete.
        if (_vm.Connection is not null)
            SchemaCache.GetWords(_vm.Connection);
    }

    // ---- keyboard: Ctrl+Space completion, Ctrl+O/Ctrl+S files ---------------------------

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ShowCompletion();
            e.Handled = true;
        }
        else if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _vm?.OpenFileCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _vm?.SaveFileCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ShowCompletion()
    {
        if (_vm is null) return;

        // Word being typed (used to seed the filter).
        var caret = Editor.CaretOffset;
        var start = caret;
        while (start > 0 && (char.IsLetterOrDigit(Editor.Document.GetCharAt(start - 1))
                             || Editor.Document.GetCharAt(start - 1) == '_'))
            start--;
        var prefix = Editor.Document.GetText(start, caret - start);

        var schemaWords = _vm.Connection is not null
            ? SchemaCache.GetWords(_vm.Connection)
            : Array.Empty<string>();

        var candidates = SchemaCache.SqlKeywords
            .Concat(schemaWords)
            .Where(w => prefix.Length == 0 || w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(w => w)
            .Take(200)
            .ToList();
        if (candidates.Count == 0) return;

        _completionWindow = new CompletionWindow(Editor.TextArea)
        {
            StartOffset = start,
            EndOffset = caret
        };
        foreach (var word in candidates)
            _completionWindow.CompletionList.CompletionData.Add(new SqlCompletionData(word));
        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    private sealed class SqlCompletionData : ICompletionData
    {
        public SqlCompletionData(string text) => Text = text;
        public string Text { get; }
        public object Content => Text;
        public object Description => Text;
        public Avalonia.Media.IImage? Image => null;
        public double Priority => 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            => textArea.Document.Replace(completionSegment, Text);
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
        if (_syncingText || _vm is null) return;
        _vm.DocumentText = Editor.Text;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryDocumentViewModel.SelectedResult))
            RebuildColumns(_vm?.SelectedResult);
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
