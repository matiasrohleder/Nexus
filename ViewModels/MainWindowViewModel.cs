using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Models;
using Nexus.Providers;
using Nexus.Services;

namespace Nexus.ViewModels;

/// <summary>Root view model: owns connections, the object explorer, query tabs and the profiler.</summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ConnectionStore _store = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private int _queryCounter;

    /// <summary>True = sidebar on the left, false = on the right. Persisted across sessions.</summary>
    [ObservableProperty] private bool _sidebarOnLeft = true;

    public ObservableCollection<SavedConnection> Connections { get; } = new();
    public ObservableCollection<TreeNodeViewModel> ConnectionNodes { get; } = new();
    public ObservableCollection<DocumentViewModel> Documents { get; } = new();
    public ProfilerViewModel Profiler { get; } = new();

    public ObservableCollection<Services.HistoryEntry> History => Services.QueryHistoryStore.Instance.History;
    public ObservableCollection<Services.Snippet> Snippets => Services.QueryHistoryStore.Instance.Snippets;

    [ObservableProperty] private TreeNodeViewModel? _selectedNode;
    [ObservableProperty] private DocumentViewModel? _selectedDocument;

    public bool HasDocuments => Documents.Count > 0;
    public bool HasConnections => Connections.Count > 0;

    /// <summary>Shown next to the app title in the top bar ("Nexus — 0.1.0").</summary>
    public string VersionText => $"— {Services.AppInfo.Version}";

    /// <summary>Wired by the View to open the connection dialog. Returns true if the user saved.</summary>
    public Func<ConnectionEditorViewModel, Task<bool>>? ShowConnectionEditor { get; set; }

    /// <summary>Wired by the View: shows a confirmation dialog (message → confirmed?).</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>Wired by the View: opens a file picker for CSV import; returns the path or null.</summary>
    public Func<Task<string?>>? PickCsvFile { get; set; }

    /// <summary>Wired by the View: save-file picker (suggested file name) → path or null.</summary>
    public Func<string, Task<string?>>? PickExportPath { get; set; }

    /// <summary>Wired by the View: open-file picker for an exported connections JSON → path or null.</summary>
    public Func<Task<string?>>? PickImportFile { get; set; }

    /// <summary>Wired by the View: shows a transient notice (import results etc.).</summary>
    public Action<string>? Notify { get; set; }

    public MainWindowViewModel()
    {
        Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocuments));
        Connections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasConnections));
        _sidebarOnLeft = _settings.SidebarOnLeft;

        foreach (var c in _store.Load())
        {
            Connections.Add(c);
            ConnectionNodes.Add(BuildConnectionNode(c));
        }
        SyncProfilerConnections();

        // Providers report opens/closes from worker threads (tree, query tabs, admin panel…).
        ConnectionStatus.Changed += id => Avalonia.Threading.Dispatcher.UIThread.Post(() => SyncConnectionStatus(id));
    }

    private void SyncConnectionStatus(Guid id)
    {
        foreach (var root in ConnectionNodes.Where(n => n.Connection?.Id == id))
        {
            root.IsConnected = ConnectionStatus.IsConnected(id);
            if (root.IsConnected) root.HasConnectError = false;
        }
    }

    /// <summary>The connection root a node belongs to (children may hold database-pinned clones).</summary>
    private TreeNodeViewModel? RootOf(TreeNodeViewModel? node) =>
        node is { IsConnection: true }
            ? node
            : ConnectionNodes.FirstOrDefault(n => n.Connection?.Id == node?.Connection?.Id);

    // ---- Object explorer node factory --------------------------------------------------

    private TreeNodeViewModel BuildConnectionNode(SavedConnection c)
    {
        var provider = ProviderRegistry.For(c);
        var node = new TreeNodeViewModel(c.Name, "database", NodeKind.Connection, LoadContainersAsync)
        {
            Connection = c,
            Badge = provider.Badge,
            BadgeColor = provider.BrandColor,
            AccentColor = c.Color,
            IsConnected = ConnectionStatus.IsConnected(c.Id)
        };
        return node;
    }

    /// <summary>Expanding a connection root is what connects it; a failure marks it red and closes what opened.</summary>
    private async Task LoadContainersAsync(TreeNodeViewModel node)
    {
        try
        {
            await LoadServerLevelAsync(node);
            node.HasConnectError = false;
        }
        catch
        {
            node.HasConnectError = true;
            await ProviderRegistry.For(node.Connection!).DisconnectAsync(node.Connection!);
            throw;
        }
    }

    private async Task LoadServerLevelAsync(TreeNodeViewModel node)
    {
        var provider = ProviderRegistry.For(node.Connection!);

        // Server-level databases (PostgreSQL, SQL Server): one node per database; children
        // browse via a cloned connection pinned to that database.
        var databases = await provider.GetDatabasesAsync(node.Connection!);
        if (databases.Count > 0)
        {
            foreach (var dbName in databases)
            {
                var pinned = node.Connection!.Clone();
                pinned.Database = dbName;
                node.AddChild(new TreeNodeViewModel(dbName, "database", NodeKind.Database, LoadSchemasAsync)
                {
                    Connection = pinned
                });
            }
            return;
        }

        await LoadSchemasAsync(node);
    }

    private async Task LoadSchemasAsync(TreeNodeViewModel node)
    {
        var provider = ProviderRegistry.For(node.Connection!);
        var containers = await provider.GetContainersAsync(node.Connection!);
        foreach (var container in containers)
        {
            node.AddChild(new TreeNodeViewModel(container, "folder", NodeKind.Schema, LoadObjectsAsync)
            {
                Connection = node.Connection,
                Schema = container
            });
        }
        if (containers.Count == 0)
            node.AddChild(new TreeNodeViewModel("(empty)", "", NodeKind.Message, null));
    }

    private async Task LoadObjectsAsync(TreeNodeViewModel node)
    {
        var provider = ProviderRegistry.For(node.Connection!);
        var objects = await provider.GetObjectsAsync(node.Connection!, node.Schema!);
        foreach (var o in objects)
        {
            var (glyph, kind) = o.Kind switch
            {
                DbObjectKind.View => ("view", NodeKind.View),
                DbObjectKind.Collection => ("collection", NodeKind.Table),
                _ => ("table", NodeKind.Table)
            };
            node.AddChild(new TreeNodeViewModel(o.Name, glyph, kind, LoadColumnsAsync)
            {
                Connection = node.Connection,
                Schema = node.Schema,
                Table = o.Name
            });
        }
        if (objects.Count == 0)
            node.AddChild(new TreeNodeViewModel("(no objects)", "", NodeKind.Message, null));
    }

    private async Task LoadColumnsAsync(TreeNodeViewModel node)
    {
        var provider = ProviderRegistry.For(node.Connection!);
        var columns = await provider.GetColumnsAsync(node.Connection!, node.Schema!, node.Table!);
        foreach (var col in columns)
        {
            var nullable = col.Nullable ? "" : " NOT NULL";
            node.AddChild(new TreeNodeViewModel(col.Name, col.IsPrimaryKey ? "key" : "column", NodeKind.Column, null)
            {
                IconColor = col.IsPrimaryKey ? "#FBBF24" : "#9CA3AF",
                Detail = $"{col.DataType}{nullable}"
            });
        }
    }

    private void SyncProfilerConnections()
    {
        Profiler.Connections.Clear();
        foreach (var c in Connections)
            Profiler.Connections.Add(c);
    }

    private SavedConnection? ActiveConnection =>
        SelectedNode?.Connection
        ?? (SelectedDocument as QueryDocumentViewModel)?.Connection
        ?? Connections.FirstOrDefault();

    // ---- Connection commands -----------------------------------------------------------

    [RelayCommand]
    private async Task AddConnectionAsync()
    {
        if (ShowConnectionEditor is null) return;
        var vm = new ConnectionEditorViewModel(null);
        if (await ShowConnectionEditor(vm))
        {
            var saved = vm.Commit();
            Connections.Add(saved);
            ConnectionNodes.Add(BuildConnectionNode(saved));
            _store.SaveAll(Connections);
            SyncProfilerConnections();
        }
    }

    /// <summary>Resolves a (possibly database-pinned clone) connection back to the saved profile.</summary>
    private SavedConnection? ResolveSaved(SavedConnection? connection) =>
        connection is null ? null : Connections.FirstOrDefault(x => x.Id == connection.Id);

    [RelayCommand]
    private async Task EditConnectionAsync()
    {
        var connection = ResolveSaved(SelectedNode?.Connection ?? ActiveConnection);
        if (connection is null || ShowConnectionEditor is null) return;

        var vm = new ConnectionEditorViewModel(connection);
        if (await ShowConnectionEditor(vm))
        {
            // Settings may have changed: close connections opened with the old ones.
            await ProviderRegistry.For(connection).DisconnectAsync(connection);
            var updated = vm.Commit();
            var index = Connections.IndexOf(connection);
            if (index >= 0)
            {
                Connections[index] = updated;
                ConnectionNodes[index] = BuildConnectionNode(updated);
            }
            _store.SaveAll(Connections);
            SyncProfilerConnections();
        }
    }

    [RelayCommand]
    private async Task RemoveConnectionAsync()
    {
        var connection = ResolveSaved(SelectedNode?.Connection ?? ActiveConnection);
        if (connection is null) return;

        var index = Connections.IndexOf(connection);
        if (index < 0) return;

        if (Confirm is not null && !await Confirm(
                $"Remove the connection \"{connection.Name}\"?\n\n" +
                $"Its saved password is deleted from {SecretStore.LocationName}. The database itself is not touched."))
            return;

        await ProviderRegistry.For(connection).DisconnectAsync(connection);
        Connections.RemoveAt(index);
        ConnectionNodes.RemoveAt(index);
        ConnectionStore.ForgetPassword(connection);
        _store.SaveAll(Connections);
        SyncProfilerConnections();
    }

    // ---- Export / import (connections.json carries no passwords, so it's safe to move) ----

    [RelayCommand]
    private async Task ExportConnectionsAsync()
    {
        if (PickExportPath is null) return;
        if (Connections.Count == 0)
        {
            Notify?.Invoke("There are no connections to export yet.");
            return;
        }

        var path = await PickExportPath("nexus-connections.json");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            ConnectionStore.Export(Connections, path);
            Notify?.Invoke($"Exported {Connections.Count} connection(s) to:\n{path}\n\n" +
                           $"Passwords are not included — they stay in {SecretStore.LocationName} on this computer.");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"Export failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ImportConnectionsAsync()
    {
        if (PickImportFile is null) return;
        var path = await PickImportFile();
        if (string.IsNullOrEmpty(path)) return;

        List<SavedConnection> imported;
        try
        {
            imported = ConnectionStore.ReadExport(path);
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"Could not import \"{System.IO.Path.GetFileName(path)}\":\n{ex.Message}");
            return;
        }

        int added = 0, updated = 0;
        foreach (var c in imported)
        {
            var existing = ResolveSaved(c);
            if (existing is not null)
            {
                var index = Connections.IndexOf(existing);
                Connections[index] = c;
                ConnectionNodes[index] = BuildConnectionNode(c);
                updated++;
            }
            else
            {
                Connections.Add(c);
                ConnectionNodes.Add(BuildConnectionNode(c));
                added++;
            }
        }
        _store.SaveAll(Connections);
        SyncProfilerConnections();

        var message = $"Imported {added} new and updated {updated} existing connection(s).";
        if (added > 0)
            message += "\n\nPasswords never travel in the file — edit each new connection to enter its password on this computer.";
        Notify?.Invoke(message);
    }

    /// <summary>Opens the folder holding connections.json, settings and history in the OS file manager.</summary>
    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.ConfigDir)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"Your Nexus data folder is:\n{AppPaths.ConfigDir}\n\n({ex.Message})");
        }
    }

    [RelayCommand]
    private void ShowAbout() =>
        Notify?.Invoke($"Nexus {AppInfo.Version}\n\nA lightweight, cross-platform database administrator.\n\n" +
                       $"Connections: {AppPaths.ConnectionsFile}\nPasswords: {SecretStore.LocationName}");

    [RelayCommand]
    private void ToggleSidebarSide()
    {
        SidebarOnLeft = !SidebarOnLeft;
        _settings.SidebarOnLeft = SidebarOnLeft;
        _settings.Save();
    }

    /// <summary>Opens the connection by expanding its root (or reloading it after a failed attempt).</summary>
    [RelayCommand]
    private void Connect(TreeNodeViewModel? node)
    {
        var root = RootOf(node ?? SelectedNode);
        if (root is null) return;
        if (root.IsExpanded)
            _ = root.ReloadAsync();
        else
            root.IsExpanded = true;
    }

    /// <summary>
    /// Collapses the connection's tree and closes every live server connection Nexus holds for it.
    /// Open query tabs stay; running one simply reconnects.
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync(TreeNodeViewModel? node)
    {
        var root = RootOf(node ?? SelectedNode);
        if (root?.Connection is null) return;

        if (SelectedNode is not null && SelectedNode.Connection?.Id == root.Connection.Id)
            SelectedNode = root;
        root.Invalidate();
        root.HasConnectError = false;
        Services.SchemaCache.Invalidate(root.Connection.Id);
        await ProviderRegistry.For(root.Connection).DisconnectAsync(root.Connection);
    }

    [RelayCommand]
    private void RefreshConnection()
    {
        var root = RootOf(SelectedNode);
        root?.Invalidate();
        if (root?.Connection is not null)
            Services.SchemaCache.Invalidate(root.Connection.Id);
    }

    /// <summary>Context-menu Refresh: reloads that node in place (connection roots refresh as a whole).</summary>
    [RelayCommand]
    private async Task RefreshNodeAsync(TreeNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node is null || node.IsConnection)
        {
            RefreshConnection();
            return;
        }
        if (node.Connection is not null)
            Services.SchemaCache.Invalidate(node.Connection.Id);
        await node.ReloadAsync();
    }

    // ---- Query commands ----------------------------------------------------------------

    [RelayCommand]
    private void NewQuery()
    {
        var conn = ActiveConnection;
        var doc = new QueryDocumentViewModel($"Query {++_queryCounter}", conn);
        Documents.Add(doc);
        SelectedDocument = doc;
    }

    [RelayCommand]
    private void RunActive() => (SelectedDocument as QueryDocumentViewModel)?.RunCommand.Execute(null);

    [RelayCommand]
    private void CloseDocument(DocumentViewModel? doc)
    {
        if (doc is null) return;
        var index = Documents.IndexOf(doc);
        Documents.Remove(doc);
        if (SelectedDocument == doc)
            SelectedDocument = Documents.ElementAtOrDefault(Math.Min(index, Documents.Count - 1));
    }

    [RelayCommand]
    private async Task OpenTableAsync(TreeNodeViewModel? node)
    {
        if (node is null || !node.IsQueryable || node.Connection is null) return;

        var provider = ProviderRegistry.For(node.Connection);

        // Explicit, line-broken column list when introspection succeeds; falls back to *.
        List<ColumnInfo>? columns = null;
        try { columns = await provider.GetColumnsAsync(node.Connection, node.Schema!, node.Table!); }
        catch { /* fall back to SELECT * */ }

        var sql = provider.BuildSelectQuery(node.Schema!, node.Table!, 100, columns);

        // Bind the saved profile (so the Connection combo resolves) and pin the node's database.
        var saved = ResolveSaved(node.Connection) ?? node.Connection;
        var doc = new QueryDocumentViewModel(node.Table!, saved, sql);
        if (node.Connection.Database != saved.Database)
            doc.SelectedDatabase = node.Connection.Database;
        Documents.Add(doc);
        SelectedDocument = doc;
        doc.RunCommand.Execute(null);
    }

    // ---- admin commands (object-explorer context menu) --------------------------------

    [RelayCommand]
    private void EditTableData(TreeNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node is null || !node.IsQueryable || node.Connection is null) return;

        var doc = new TableEditorViewModel(node.Connection, node.Schema!, node.Table!);
        Documents.Add(doc);
        SelectedDocument = doc;
    }

    [RelayCommand]
    private async Task ViewDdlAsync(TreeNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node is null || !node.IsQueryable || node.Connection is null) return;

        var provider = ProviderRegistry.For(node.Connection);
        string ddl;
        try
        {
            ddl = await provider.GetObjectDdlAsync(node.Connection, node.Schema!, node.Table!);
        }
        catch (Exception ex)
        {
            ddl = $"-- Failed to generate DDL: {ex.Message}";
        }
        var doc = new QueryDocumentViewModel($"DDL: {node.Table}", node.Connection, ddl);
        Documents.Add(doc);
        SelectedDocument = doc;
    }

    [RelayCommand]
    private async Task DropObjectAsync(TreeNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node is null || node.Connection is null || Confirm is null) return;

        switch (node.Kind)
        {
            case NodeKind.Table or NodeKind.View:
                await DropTableOrViewAsync(node);
                break;
            case NodeKind.Database:
                await DropDatabaseAsync(node);
                break;
            case NodeKind.Schema:
                await DropContainerAsync(node);
                break;
            default:
                Notify?.Invoke("Select a table, view, schema or database to drop.");
                break;
        }
    }

    private async Task DropTableOrViewAsync(TreeNodeViewModel node)
    {
        var kind = node.Kind == NodeKind.View ? "VIEW" : "TABLE";
        if (!await Confirm!($"Drop {kind.ToLowerInvariant()} \"{node.Schema}\".\"{node.Table}\"?\nThis cannot be undone."))
            return;

        var provider = ProviderRegistry.For(node.Connection!);
        var qualified = $"{provider.QuoteIdent(node.Schema!)}.{provider.QuoteIdent(node.Table!)}";

        var results = await provider.ExecuteAsync(node.Connection!, $"DROP {kind} {qualified}");
        var error = results.FirstOrDefault(r => r.HasError)?.Error;

        // Common case: other objects depend on it → offer CASCADE (engines that support it).
        if (error is not null && DialectSupportsCascade(provider.Id) && LooksLikeDependencyError(error))
        {
            if (await Confirm!($"Cannot drop — other objects depend on it:\n\n{error}\n\nDrop with CASCADE (also removes dependents)?"))
            {
                results = await provider.ExecuteAsync(node.Connection!, $"DROP {kind} {qualified} CASCADE");
                error = results.FirstOrDefault(r => r.HasError)?.Error;
            }
            else return;
        }

        if (error is not null)
        {
            Notify?.Invoke($"Drop failed: {error}");
            return;
        }

        Notify?.Invoke($"Dropped {kind.ToLowerInvariant()} {node.Schema}.{node.Table}.");
        await RefreshAfterSchemaChangeAsync(node);
    }

    private async Task DropDatabaseAsync(TreeNodeViewModel node)
    {
        var provider = ProviderRegistry.For(node.Connection!);
        if (provider is not SqlProviderBase sqlProvider ||
            sqlProvider.BuildDropDatabaseSql(node.Name, force: false) is not { } dropSql)
        {
            Notify?.Invoke($"{provider.DisplayName} does not support dropping databases from Nexus.");
            return;
        }

        if (!await Confirm!($"Drop DATABASE \"{node.Name}\"?\nALL schemas, tables and data in it will be permanently lost."))
            return;

        // Never run from the database being dropped: use a maintenance database.
        var admin = (ResolveSaved(node.Connection) ?? node.Connection!).Clone();
        if (string.IsNullOrEmpty(admin.Database) || admin.Database == node.Name)
            admin.Database = provider.DefaultDatabase;

        // Nexus's own pooled connections would otherwise block the drop.
        await sqlProvider.PrepareDropDatabaseAsync(admin);

        var results = await provider.ExecuteAsync(admin, dropSql);
        var error = results.FirstOrDefault(r => r.HasError)?.Error;

        // Database in use (other clients) → offer force drop (terminates their sessions).
        if (error is not null &&
            (error.Contains("accessed", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("in use", StringComparison.OrdinalIgnoreCase)) &&
            sqlProvider.BuildDropDatabaseSql(node.Name, force: true) is { } forceSql)
        {
            if (await Confirm!($"The database is in use:\n\n{error}\n\nForce drop? This terminates the other sessions."))
            {
                results = await provider.ExecuteAsync(admin, forceSql);
                error = results.FirstOrDefault(r => r.HasError)?.Error;
            }
            else return;
        }

        if (error is not null)
        {
            Notify?.Invoke($"Drop failed: {error}");
            return;
        }

        Notify?.Invoke($"Dropped database {node.Name}.");
        await RefreshAfterSchemaChangeAsync(node);
    }

    private async Task DropContainerAsync(TreeNodeViewModel node)
    {
        var provider = ProviderRegistry.For(node.Connection!);
        var dropSql = provider.BuildDropContainerSql(node.Schema!, cascade: false);
        if (dropSql is null)
        {
            Notify?.Invoke($"{provider.DisplayName} does not support dropping this from Nexus.");
            return;
        }

        var label = provider.Capabilities.SupportsSchemas ? "schema" : "database";
        if (!await Confirm!($"Drop {label} \"{node.Schema}\"?\nEverything inside it will be permanently lost."))
            return;

        // Engines whose containers are databases (Mongo) need the command to run *on* that database.
        var conn = node.Connection!;
        if (provider.Id == "mongodb")
        {
            conn = conn.Clone();
            conn.Database = node.Schema!;
        }

        var results = await provider.ExecuteAsync(conn, dropSql);
        var error = results.FirstOrDefault(r => r.HasError)?.Error;

        if (error is not null && LooksLikeDependencyError(error) &&
            provider.BuildDropContainerSql(node.Schema!, cascade: true) is { } cascadeSql && cascadeSql != dropSql)
        {
            if (await Confirm!($"Cannot drop — it is not empty:\n\n{error}\n\nDrop with CASCADE (removes everything inside)?"))
            {
                results = await provider.ExecuteAsync(conn, cascadeSql);
                error = results.FirstOrDefault(r => r.HasError)?.Error;
            }
            else return;
        }

        if (error is not null)
        {
            Notify?.Invoke($"Drop failed: {error}");
            return;
        }

        Notify?.Invoke($"Dropped {label} {node.Schema}.");
        await RefreshAfterSchemaChangeAsync(node);
    }

    private static bool LooksLikeDependencyError(string error) =>
        error.Contains("depend", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("foreign key", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("not empty", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("CASCADE", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task TruncateTableAsync(TreeNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node is null || node.Kind != NodeKind.Table || node.Connection is null || Confirm is null) return;

        if (!await Confirm($"Truncate table \"{node.Schema}\".\"{node.Table}\"?\nAll rows will be deleted."))
            return;

        var provider = ProviderRegistry.For(node.Connection);
        var qualified = $"{provider.QuoteIdent(node.Schema!)}.{provider.QuoteIdent(node.Table!)}";
        // SQLite has no TRUNCATE — DELETE is its idiom.
        var sql = provider.Id == "sqlite" ? $"DELETE FROM {qualified}" : $"TRUNCATE TABLE {qualified}";

        var results = await provider.ExecuteAsync(node.Connection, sql);
        var error = results.FirstOrDefault(r => r.HasError)?.Error;
        Notify?.Invoke(error is null
            ? $"Truncated {node.Schema}.{node.Table}."
            : $"Truncate failed: {error}");
    }

    private static bool DialectSupportsCascade(string providerId) =>
        providerId is "postgresql" or "oracle" or "clickhouse";

    /// <summary>Reloads the dropped object's parent schema in place so the tree reflects the change.</summary>
    private async Task RefreshAfterSchemaChangeAsync(TreeNodeViewModel node)
    {
        if (node.Parent is not null)
            await node.Parent.ReloadAsync();
        else
            RefreshConnection();
    }

    [RelayCommand]
    private async Task ImportCsvAsync(TreeNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node is null || node.Kind != NodeKind.Table || node.Connection is null || PickCsvFile is null) return;

        var path = await PickCsvFile();
        if (path is null) return;

        var provider = ProviderRegistry.For(node.Connection);
        try
        {
            var rows = Services.CsvService.Parse(await System.IO.File.ReadAllTextAsync(path));
            if (rows.Count < 2)
            {
                Notify?.Invoke("CSV needs a header row plus at least one data row.");
                return;
            }

            // Map CSV headers to actual table columns (case-insensitive).
            var tableColumns = await provider.GetColumnsAsync(node.Connection, node.Schema!, node.Table!);
            var headers = rows[0];
            var mapped = new List<(int csvIndex, ColumnInfo column)>();
            for (int i = 0; i < headers.Length; i++)
            {
                var col = tableColumns.FirstOrDefault(x =>
                    x.Name.Equals(headers[i].Trim(), StringComparison.OrdinalIgnoreCase));
                if (col is not null) mapped.Add((i, col));
            }
            if (mapped.Count == 0)
            {
                Notify?.Invoke("No CSV headers match the table's columns.");
                return;
            }

            var qualified = $"{provider.QuoteIdent(node.Schema!)}.{provider.QuoteIdent(node.Table!)}";
            var colList = string.Join(", ", mapped.Select(m => provider.QuoteIdent(m.column.Name)));

            var statements = new List<string>();
            foreach (var row in rows.Skip(1))
            {
                var values = mapped.Select(m =>
                {
                    var v = m.csvIndex < row.Length ? row[m.csvIndex] : "";
                    if (v.Length == 0) return "NULL";
                    var t = m.column.DataType.ToLowerInvariant();
                    var numeric = t.Contains("int") || t.Contains("numeric") || t.Contains("decimal")
                        || t.Contains("float") || t.Contains("double") || t.Contains("real") || t.Contains("number");
                    return numeric && decimal.TryParse(v, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out _)
                        ? v
                        : provider.QuoteLiteral(v);
                });
                statements.Add($"INSERT INTO {qualified} ({colList}) VALUES ({string.Join(", ", values)})");
            }

            var n = await provider.ExecuteTransactionAsync(node.Connection, statements);
            Notify?.Invoke($"Imported {n} row(s) into {node.Table} (columns: {mapped.Count} matched).");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"Import failed (rolled back): {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenFromHistory(Services.HistoryEntry? entry)
    {
        if (entry is null) return;
        var conn = Connections.FirstOrDefault(c => c.Name == entry.Connection) ?? ActiveConnection;
        var doc = new QueryDocumentViewModel($"Query {++_queryCounter}", conn, entry.Sql);
        Documents.Add(doc);
        SelectedDocument = doc;
    }

    [RelayCommand]
    private void OpenSnippet(Services.Snippet? snippet)
    {
        if (snippet is null) return;
        var doc = new QueryDocumentViewModel(snippet.Name, ActiveConnection, snippet.Sql);
        Documents.Add(doc);
        SelectedDocument = doc;
    }

    [RelayCommand]
    private void DeleteSnippet(Services.Snippet? snippet)
    {
        if (snippet is null) return;
        Services.QueryHistoryStore.Instance.RemoveSnippet(snippet);
    }

    [RelayCommand]
    private void ClearHistory() => Services.QueryHistoryStore.Instance.ClearHistory();

    [RelayCommand]
    private void OpenServerAdmin(TreeNodeViewModel? node)
    {
        node ??= SelectedNode;
        var connection = node?.Connection ?? ActiveConnection;
        if (connection is null) return;

        var doc = new ServerAdminViewModel(connection);
        Documents.Add(doc);
        SelectedDocument = doc;
    }
}
