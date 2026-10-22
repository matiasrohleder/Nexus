using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Models;
using Nexus.Providers;

namespace Nexus.ViewModels;

/// <summary>One SQL editor tab: its text, target connection, results grid, and Run/Stop commands.</summary>
public partial class QueryDocumentViewModel : DocumentViewModel
{
    public override string Icon => "query";

    private CancellationTokenSource? _runCts;

    [ObservableProperty] private string _documentText;
    [ObservableProperty] private SavedConnection? _connection;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _messagesText = string.Empty;

    /// <summary>True when the last run failed — drives the red error banner over the results.</summary>
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultipleResults))]
    private QueryResult? _selectedResult;

    public ObservableCollection<QueryResult> Results { get; } = new();
    public bool HasMultipleResults => Results.Count > 1;

    /// <summary>Set by the view; returns the editor's current selection (empty if none selected).</summary>
    public Func<string?>? SelectionProvider { get; set; }

    public string ConnectionName => Connection?.Name ?? "Not connected";

    public QueryDocumentViewModel(string title, SavedConnection? connection, string initialText = "")
    {
        Title = title;
        _connection = connection;
        _documentText = initialText;
        _ = LoadDatabasesAsync();
    }

    /// <summary>
    /// Two-way target for the connection picker. Ignores the null a ComboBox pushes while it
    /// re-binds (e.g. when switching back to this tab), which used to silently disconnect the tab,
    /// and keeps a database-pinned clone when the picker reports the same profile.
    /// </summary>
    public SavedConnection? ConnectionChoice
    {
        get => Connection;
        set
        {
            if (value is not null && value.Id != Connection?.Id) Connection = value;
        }
    }

    /// <summary>Two-way target for the database picker; same null guard as <see cref="ConnectionChoice"/>.</summary>
    public string? DatabaseChoice
    {
        get => SelectedDatabase;
        set
        {
            if (value is not null) SelectedDatabase = value;
        }
    }

    partial void OnSelectedDatabaseChanged(string? value) => OnPropertyChanged(nameof(DatabaseChoice));

    partial void OnConnectionChanged(SavedConnection? value)
    {
        OnPropertyChanged(nameof(ConnectionChoice));
        OnPropertyChanged(nameof(ConnectionName));
        OnPropertyChanged(nameof(SupportsExplain));
        SelectedDatabase = null;
        _ = LoadDatabasesAsync();
    }

    // ---- database picker (engines where one server hosts many databases) ----------------

    public ObservableCollection<string> Databases { get; } = new();

    [ObservableProperty] private string? _selectedDatabase;

    public bool HasDatabases => Databases.Count > 0;

    private async Task LoadDatabasesAsync()
    {
        Databases.Clear();
        OnPropertyChanged(nameof(HasDatabases));
        if (Connection is null) return;

        var provider = ProviderRegistry.For(Connection);
        if (provider.Capabilities.IsFileBased) return;

        try
        {
            // PG/MSSQL list server databases; for engines whose containers already are
            // databases (MySQL, Mongo, ClickHouse, Redis) the containers fill the picker.
            var dbs = await provider.GetDatabasesAsync(Connection);
            if (dbs.Count == 0 && !provider.Capabilities.SupportsSchemas)
                dbs = await provider.GetContainersAsync(Connection);

            foreach (var db in dbs)
                Databases.Add(db);

            if (SelectedDatabase is null || !dbs.Contains(SelectedDatabase))
                SelectedDatabase = dbs.Contains(Connection.Database) ? Connection.Database : dbs.FirstOrDefault();
        }
        catch { /* picker stays hidden on failure */ }
        OnPropertyChanged(nameof(HasDatabases));
    }

    /// <summary>Connection actually used to run: pinned to the picked database when it differs.</summary>
    private SavedConnection? EffectiveConnection
    {
        get
        {
            if (Connection is null) return null;
            if (string.IsNullOrEmpty(SelectedDatabase) || SelectedDatabase == Connection.Database)
                return Connection;
            var pinned = Connection.Clone();
            pinned.Database = SelectedDatabase;
            return pinned;
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning) return;

        if (Connection is null)
        {
            ShowError("No connection assigned to this query.");
            return;
        }

        // Run the selection if the user highlighted something, otherwise the whole document.
        var selection = SelectionProvider?.Invoke();
        var sql = !string.IsNullOrWhiteSpace(selection) ? selection! : DocumentText;
        if (string.IsNullOrWhiteSpace(sql))
        {
            StatusText = "Nothing to run.";
            return;
        }

        IsRunning = true;
        HasError = false;
        StatusText = "Executing…";
        _runCts = new CancellationTokenSource();
        try
        {
            var effective = EffectiveConnection!;
            var provider = ProviderRegistry.For(effective);
            var results = await provider.ExecuteAsync(effective, sql, _runCts.Token);

            Services.QueryHistoryStore.Instance.RecordRun(
                Connection.Name, sql,
                !results.Any(r => r.HasError),
                results.FirstOrDefault()?.DurationMs ?? 0);

            Results.Clear();
            foreach (var r in results)
                Results.Add(r);

            OnPropertyChanged(nameof(HasMultipleResults));
            SelectedResult = Results.FirstOrDefault(r => r.HasResultSet) ?? Results.FirstOrDefault();

            var error = results.FirstOrDefault(r => r.HasError);
            if (error is not null)
            {
                var label = _runCts.IsCancellationRequested ? "Cancelled" : "Error";
                ShowError(error.Error ?? "Unknown error", label);
            }
            else
            {
                var duration = results.FirstOrDefault()?.DurationMs ?? 0;
                var totalRows = results.Sum(r => r.Rows.Count);
                HasError = false;
                StatusText = $"Success · {totalRows} row(s) · {duration:0} ms";
                MessagesText = string.Join(Environment.NewLine,
                    results.Select((r, i) => $"[{i + 1}] {r.Message} ({r.DurationMs:0} ms)"));
            }
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            IsRunning = false;
        }
    }

    /// <summary>Surfaces an error in the status line, the Messages tab and the red error banner.</summary>
    private void ShowError(string message, string status = "Error")
    {
        HasError = true;
        ErrorText = message;
        MessagesText = message;
        StatusText = status;
    }

    [RelayCommand]
    private void Stop()
    {
        try { _runCts?.Cancel(); } catch { }
        StatusText = "Cancelling…";
    }

    // ---- EXPLAIN -----------------------------------------------------------------------

    public bool SupportsExplain =>
        Connection is not null && ProviderRegistry.For(Connection).ExplainPrefix is not null;

    [RelayCommand]
    private async Task ExplainAsync()
    {
        if (Connection is null || IsRunning) return;
        var prefix = ProviderRegistry.For(Connection).ExplainPrefix;
        if (prefix is null) return;

        var selection = SelectionProvider?.Invoke();
        var sql = !string.IsNullOrWhiteSpace(selection) ? selection! : DocumentText;
        if (string.IsNullOrWhiteSpace(sql)) return;

        var original = DocumentText;
        try
        {
            // Reuse the Run pipeline with the EXPLAIN-prefixed statement.
            DocumentText = prefix + sql.TrimStart();
            await RunAsync();
        }
        finally
        {
            DocumentText = original;
        }
    }

    // ---- Snippets / files ----------------------------------------------------------------

    /// <summary>Wired by the view: prompts for a short text (snippet name); null = cancelled.</summary>
    public Func<string, Task<string?>>? PromptText { get; set; }

    /// <summary>Wired by the view: asks for a path to open a .sql file.</summary>
    public Func<Task<string?>>? PickOpenPath { get; set; }

    /// <summary>Backing file for Save (set after Open or Save-as).</summary>
    [ObservableProperty] private string? _filePath;

    [RelayCommand]
    private async Task SaveSnippetAsync()
    {
        if (PromptText is null || string.IsNullOrWhiteSpace(DocumentText)) return;
        var name = await PromptText("Snippet name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        Services.QueryHistoryStore.Instance.AddSnippet(name.Trim(), DocumentText);
        StatusText = $"Snippet \"{name.Trim()}\" saved.";
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (PickOpenPath is null) return;
        var path = await PickOpenPath();
        if (path is null) return;
        DocumentText = await System.IO.File.ReadAllTextAsync(path);
        FilePath = path;
        Title = System.IO.Path.GetFileName(path);
        StatusText = $"Opened {path}";
    }

    [RelayCommand]
    private async Task SaveFileAsync()
    {
        var path = FilePath;
        if (path is null)
        {
            if (PickSavePath is null) return;
            path = await PickSavePath(Title.EndsWith(".sql") ? Title : $"{Title}.sql");
            if (path is null) return;
            FilePath = path;
            Title = System.IO.Path.GetFileName(path);
        }
        await System.IO.File.WriteAllTextAsync(path, DocumentText);
        StatusText = $"Saved {path}";
    }

    // ---- Export ------------------------------------------------------------------------

    /// <summary>Wired by the view: asks for a save path with the given suggested name/extension.</summary>
    public Func<string, Task<string?>>? PickSavePath { get; set; }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var rs = SelectedResult;
        if (rs is null || !rs.HasResultSet || PickSavePath is null) return;

        var path = await PickSavePath("results.csv");
        if (path is null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Services.CsvService.BuildLine(rs.Columns));
        foreach (var row in rs.Rows)
        {
            var cells = new List<string?>();
            for (int i = 0; i < rs.Columns.Count; i++)
            {
                var raw = row.GetRaw(i);
                cells.Add(raw is null or DBNull ? null : EditableRow.ToCellText(raw));
            }
            sb.AppendLine(Services.CsvService.BuildLine(cells));
        }
        await System.IO.File.WriteAllTextAsync(path, sb.ToString());
        StatusText = $"Exported {rs.Rows.Count} row(s) to {System.IO.Path.GetFileName(path)}";
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        var rs = SelectedResult;
        if (rs is null || !rs.HasResultSet || PickSavePath is null) return;

        var path = await PickSavePath("results.json");
        if (path is null) return;

        var list = new List<Dictionary<string, object?>>();
        foreach (var row in rs.Rows)
        {
            var obj = new Dictionary<string, object?>();
            for (int i = 0; i < rs.Columns.Count; i++)
            {
                var raw = row.GetRaw(i);
                obj[rs.Columns[i]] = raw switch
                {
                    null or DBNull => null,
                    bool or string or int or long or double or decimal or float or short or byte => raw,
                    _ => EditableRow.ToCellText(raw)
                };
            }
            list.Add(obj);
        }
        var json = System.Text.Json.JsonSerializer.Serialize(list,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await System.IO.File.WriteAllTextAsync(path, json);
        StatusText = $"Exported {rs.Rows.Count} row(s) to {System.IO.Path.GetFileName(path)}";
    }
}
