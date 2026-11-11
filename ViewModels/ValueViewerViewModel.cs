using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Services;

namespace Nexus.ViewModels;

/// <summary>
/// "Value" tab: one cell's full value in an editor, pretty-printed and highlighted when it's JSON
/// or XML. Read-only for query results; from the data editor (editable table) the value can be
/// changed and written back to the cell, which then commits with the editor's other changes.
/// </summary>
public partial class ValueViewerViewModel : DocumentViewModel
{
    /// <summary>Writes the text back to the source cell; returns an error message or null.</summary>
    private readonly Func<string, string?>? _apply;

    public ValueViewerViewModel(string title, string columnName, string? typeName, string value,
        ValueKind columnKind, object sourceKey, Func<string, string?>? apply = null)
    {
        Title = title;
        ColumnName = columnName;
        TypeName = typeName;
        OriginalText = value;
        SourceKey = sourceKey;
        _apply = apply;

        // The column's kind decides when known; otherwise look at the value itself.
        var kind = columnKind != ValueKind.Text ? columnKind : ValueFormats.Detect(value);
        _kind = kind;
        _text = ValueFormats.Pretty(value, kind);
        UpdateStatus();
    }

    public override string Icon => "braces";

    public string ColumnName { get; }
    public string? TypeName { get; }
    public string OriginalText { get; }

    /// <summary>Identifies the source cell (row object + column), so opening it again reuses this tab.</summary>
    public object SourceKey { get; }

    public bool CanApply => _apply is not null;
    public bool IsReadOnly => _apply is null;

    private static readonly ValueKind[] KindOrder = { ValueKind.Json, ValueKind.Xml, ValueKind.Text };

    /// <summary>"View as" choices, in <see cref="KindIndex"/> order.</summary>
    public IReadOnlyList<string> KindLabels { get; } = Array.ConvertAll(KindOrder, ValueFormats.Label);

    /// <summary>
    /// ComboBox proxy for <see cref="Kind"/>. Ignores the -1 a ComboBox pushes while its tab's view
    /// re-binds (same issue as the query tab's connection picker).
    /// </summary>
    public int KindIndex
    {
        get => Array.IndexOf(KindOrder, Kind);
        set
        {
            if (value < 0 || value >= KindOrder.Length) return;
            Kind = KindOrder[value];
        }
    }

    /// <summary>Editor contents (synced manually with the AvaloniaEdit control by the view).</summary>
    [ObservableProperty] private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStructured), nameof(KindIndex))]
    private ValueKind _kind;

    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _hasError;

    /// <summary>Set by the view: copies text to the clipboard.</summary>
    public Func<string, System.Threading.Tasks.Task>? CopyToClipboard { get; set; }

    public bool IsStructured => Kind != ValueKind.Text;

    public string Header => TypeName is { Length: > 0 } ? $"{ColumnName}  ·  {TypeName}" : ColumnName;

    partial void OnTextChanged(string value) => UpdateStatus();

    partial void OnKindChanged(ValueKind value)
    {
        // Switching to JSON/XML formats the text right away when it parses.
        if (value != ValueKind.Text && ValueFormats.Validate(Text, value) is null)
            Text = ValueFormats.Pretty(Text, value);
        UpdateStatus();
    }

    [RelayCommand]
    private void Format() => Reformat(indented: true);

    [RelayCommand]
    private void Compact() => Reformat(indented: false);

    private void Reformat(bool indented)
    {
        string result;
        string? error;
        bool ok;
        switch (Kind)
        {
            case ValueKind.Json: ok = ValueFormats.TryFormatJson(Text, indented, out result, out error); break;
            case ValueKind.Xml: ok = ValueFormats.TryFormatXml(Text, indented, out result, out error); break;
            default: return;
        }
        if (ok) Text = result;
        else ShowError(error);
    }

    [RelayCommand]
    private void ToggleWrap() => WordWrap = !WordWrap;

    [RelayCommand]
    private async System.Threading.Tasks.Task CopyAsync()
    {
        if (CopyToClipboard is null) return;
        await CopyToClipboard(Text);
        StatusText = "Copied to the clipboard.";
        HasError = false;
    }

    /// <summary>Restores the cell's value as it was when the tab opened (formatted).</summary>
    [RelayCommand]
    private void Revert() => Text = ValueFormats.Pretty(OriginalText, Kind);

    [RelayCommand]
    private void Apply()
    {
        if (_apply is null) return;
        if (ValueFormats.Validate(Text, Kind) is { } invalid)
        {
            ShowError($"Not valid {ValueFormats.Label(Kind)} — fix it or switch the view to Text: {invalid}");
            return;
        }
        if (_apply(Text) is { } error)
        {
            ShowError(error);
            return;
        }
        StatusText = "Applied to the cell — Commit in the data editor to save it.";
        HasError = false;
    }

    private void ShowError(string? message)
    {
        StatusText = message ?? "Invalid value.";
        HasError = true;
    }

    private void UpdateStatus()
    {
        var lines = 1;
        foreach (var c in Text) if (c == '\n') lines++;
        var size = $"{Text.Length:N0} chars · {lines:N0} line(s)";
        // Validation re-parses on every edit; skip it for huge values to keep typing responsive.
        if (Kind != ValueKind.Text && Text.Length <= 5_000_000 && ValueFormats.Validate(Text, Kind) is { } error)
        {
            StatusText = $"{size} · invalid {ValueFormats.Label(Kind)}: {error}";
            HasError = true;
        }
        else
        {
            StatusText = Kind == ValueKind.Text ? size : $"{size} · valid {ValueFormats.Label(Kind)}";
            HasError = false;
        }
    }
}
