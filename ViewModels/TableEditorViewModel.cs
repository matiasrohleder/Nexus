using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Models;
using Nexus.Providers;

namespace Nexus.ViewModels;

/// <summary>A grid row being edited. Cell text of "(null)" means SQL NULL (same convention as the viewer).</summary>
public sealed partial class EditableRow : ObservableObject
{
    public const string NullText = "(null)";

    /// <summary>Original raw values from the database; null for brand-new rows.</summary>
    public object?[]? Original { get; }
    private readonly string?[] _current;

    public bool IsNew => Original is null;

    public EditableRow(object?[]? original, int columnCount)
    {
        Original = original;
        _current = new string?[columnCount];
        if (original is not null)
            for (int i = 0; i < columnCount; i++)
                _current[i] = ToCellText(original[i]);
    }

    public string this[int index]
    {
        get => _current[index] ?? NullText;
        set
        {
            _current[index] = value == NullText ? null : value;
            OnPropertyChanged("Item[]");
        }
    }

    /// <summary>Edited value: null = SQL NULL.</summary>
    public string? GetCurrent(int index) => _current[index];

    public bool IsCellDirty(int index) =>
        Original is not null && _current[index] != ToCellText(Original[index]);

    public bool IsDirty(int columnCount)
    {
        if (IsNew) return true;
        for (int i = 0; i < columnCount; i++)
            if (IsCellDirty(i)) return true;
        return false;
    }

    internal static string? ToCellText(object? value) => value switch
    {
        null or DBNull => null,
        bool b => b ? "true" : "false",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}

/// <summary>
/// "Edit Data" tab: loads a table into an editable grid, tracks inserts/updates/deletes and
/// commits them in a single transaction. Requires a primary key (otherwise read-only).
/// </summary>
public partial class TableEditorViewModel : DocumentViewModel
{
    private const int LoadLimit = 1000;

    public SavedConnection Connection { get; }
    public string Container { get; }
    public string Table { get; }

    private IDatabaseProvider Provider => ProviderRegistry.For(Connection);
    private List<ColumnInfo> _columns = new();
    private readonly List<EditableRow> _deleted = new();

    public ObservableCollection<string> ColumnNames { get; } = new();
    public ObservableCollection<EditableRow> Rows { get; } = new();

    [ObservableProperty] private EditableRow? _selectedRow;
    [ObservableProperty] private bool _isReadOnly = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Loading…";

    /// <summary>Raised after (re)load so the view can rebuild grid columns.</summary>
    public event EventHandler? SchemaLoaded;

    public TableEditorViewModel(SavedConnection connection, string container, string table)
    {
        Connection = connection;
        Container = container;
        Table = table;
        Title = $"✎ {table}";
        _ = LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            _deleted.Clear();
            _columns = await Provider.GetColumnsAsync(Connection, Container, Table);
            var hasPk = _columns.Any(c => c.IsPrimaryKey);
            IsReadOnly = !hasPk || !Provider.Capabilities.SupportsRowEditing;

            var results = await Provider.ExecuteAsync(Connection,
                Provider.BuildSelectQuery(Container, Table, LoadLimit, _columns));
            var rs = results.FirstOrDefault(r => r.HasResultSet);

            ColumnNames.Clear();
            Rows.Clear();
            if (rs is null)
            {
                StatusText = results.FirstOrDefault()?.Error ?? "No data.";
                return;
            }

            foreach (var col in rs.Columns)
                ColumnNames.Add(col);
            foreach (var row in rs.Rows)
                Rows.Add(new EditableRow(Enumerable.Range(0, row.Count).Select(row.GetRaw).ToArray(), rs.Columns.Count));

            StatusText = IsReadOnly
                ? $"{Rows.Count} row(s) — read-only ({(Provider.Capabilities.SupportsRowEditing ? "no primary key" : "engine does not support row editing")})"
                : $"{Rows.Count} row(s) — editable (double-click a cell)";
            SchemaLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddRow()
    {
        if (IsReadOnly) return;
        var row = new EditableRow(null, ColumnNames.Count);
        Rows.Add(row);
        SelectedRow = row;
        UpdatePendingStatus();
    }

    [RelayCommand]
    private void DeleteRow()
    {
        if (IsReadOnly || SelectedRow is null) return;
        if (!SelectedRow.IsNew)
            _deleted.Add(SelectedRow);
        Rows.Remove(SelectedRow);
        UpdatePendingStatus();
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (IsReadOnly || IsBusy) return;

        var statements = BuildStatements();
        if (statements.Count == 0)
        {
            StatusText = "No pending changes.";
            return;
        }

        IsBusy = true;
        try
        {
            var n = await Provider.ExecuteTransactionAsync(Connection, statements);
            StatusText = $"Committed {n} change(s).";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Commit failed (rolled back): {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevertAsync() => await LoadAsync();

    public void UpdatePendingStatus()
    {
        if (IsReadOnly) return;
        var pending = Rows.Count(r => r.IsDirty(ColumnNames.Count)) + _deleted.Count;
        if (pending > 0)
            StatusText = $"{pending} pending change(s) — Commit to apply";
    }

    // ---- SQL generation ---------------------------------------------------------------

    private List<string> BuildStatements()
    {
        var statements = new List<string>();
        var qualified = Provider.Capabilities.SupportsSchemas || Container != "main"
            ? $"{Provider.QuoteIdent(Container)}.{Provider.QuoteIdent(Table)}"
            : Provider.QuoteIdent(Table);

        foreach (var row in _deleted)
            statements.Add($"DELETE FROM {qualified} WHERE {BuildPkWhere(row)}");

        foreach (var row in Rows)
        {
            if (row.IsNew)
            {
                var cols = new List<string>();
                var vals = new List<string>();
                for (int i = 0; i < ColumnNames.Count; i++)
                {
                    var v = row.GetCurrent(i);
                    if (v is null) continue; // omitted → engine default / NULL
                    cols.Add(Provider.QuoteIdent(ColumnNames[i]));
                    vals.Add(FormatValue(i, v));
                }
                if (cols.Count > 0)
                    statements.Add($"INSERT INTO {qualified} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)})");
            }
            else if (row.IsDirty(ColumnNames.Count))
            {
                var sets = new List<string>();
                for (int i = 0; i < ColumnNames.Count; i++)
                {
                    if (!row.IsCellDirty(i)) continue;
                    var v = row.GetCurrent(i);
                    sets.Add($"{Provider.QuoteIdent(ColumnNames[i])} = {(v is null ? "NULL" : FormatValue(i, v))}");
                }
                if (sets.Count > 0)
                    statements.Add($"UPDATE {qualified} SET {string.Join(", ", sets)} WHERE {BuildPkWhere(row)}");
            }
        }

        return statements;
    }

    private string BuildPkWhere(EditableRow row)
    {
        var parts = new List<string>();
        for (int i = 0; i < ColumnNames.Count && i < _columns.Count; i++)
        {
            var col = _columns.FirstOrDefault(x => x.Name.Equals(ColumnNames[i], StringComparison.OrdinalIgnoreCase));
            if (col is null || !col.IsPrimaryKey) continue;

            var original = EditableRow.ToCellText(row.Original![i]);
            parts.Add(original is null
                ? $"{Provider.QuoteIdent(ColumnNames[i])} IS NULL"
                : $"{Provider.QuoteIdent(ColumnNames[i])} = {FormatValue(i, original)}");
        }
        if (parts.Count == 0)
            throw new InvalidOperationException("No primary-key columns available for WHERE clause.");
        return string.Join(" AND ", parts);
    }

    /// <summary>Numeric/bool column types are emitted bare; everything else as a quoted literal.</summary>
    private string FormatValue(int columnIndex, string value)
    {
        var col = _columns.FirstOrDefault(x =>
            x.Name.Equals(ColumnNames[columnIndex], StringComparison.OrdinalIgnoreCase));
        var type = (col?.DataType ?? "").ToLowerInvariant();

        bool numeric = type.Contains("int") || type.Contains("numeric") || type.Contains("decimal")
            || type.Contains("float") || type.Contains("double") || type.Contains("real")
            || type.Contains("number") || type.Contains("money");
        if (numeric && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return value;

        bool boolean = type.Contains("bool");
        if (boolean && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("false", StringComparison.OrdinalIgnoreCase)))
            return value.ToLowerInvariant();

        return Provider.QuoteLiteral(value);
    }
}
