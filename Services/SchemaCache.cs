using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Providers;

namespace Nexus.Services;

/// <summary>
/// What the editor knows about one connection+database: containers, their objects, and the
/// columns of tables loaded so far. Objects load once up front; columns load on demand (when a
/// query references the table) so big schemas never stall the editor.
/// </summary>
public sealed class SchemaSnapshot
{
    internal SchemaSnapshot(SavedConnection connection) => Connection = connection;

    public SavedConnection Connection { get; }

    /// <summary>Container whose objects can be referenced unqualified ("public", "dbo", the current database…).</summary>
    public string? DefaultContainer { get; internal set; }

    public IReadOnlyList<string> Containers { get; internal set; } = Array.Empty<string>();

    /// <summary>container → objects.</summary>
    public IReadOnlyDictionary<string, List<DbObjectInfo>> Objects { get; internal set; } =
        new Dictionary<string, List<DbObjectInfo>>(StringComparer.OrdinalIgnoreCase);

    internal readonly ConcurrentDictionary<string, List<ColumnInfo>> Columns = new(StringComparer.OrdinalIgnoreCase);
    internal readonly ConcurrentDictionary<string, Task> ColumnLoads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every table/view/collection name (for highlighting).</summary>
    public HashSet<string> ObjectNames { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every column name loaded so far (for highlighting). Replaced, never mutated, so readers need no lock.</summary>
    public HashSet<string> ColumnNames { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLoaded { get; internal set; }

    internal static string ColumnKey(string container, string table) => container + "\u001F" + table;

    /// <summary>Columns of a table if already loaded; null otherwise (see <see cref="SchemaCache.EnsureColumnsAsync"/>).</summary>
    public List<ColumnInfo>? GetColumns(string container, string table) =>
        Columns.TryGetValue(ColumnKey(container, table), out var cols) ? cols : null;

    /// <summary>Finds which container holds <paramref name="table"/> (explicit one, else default, else any).</summary>
    public (string container, DbObjectInfo obj)? Resolve(string? container, string table)
    {
        if (container is not null)
            return Objects.TryGetValue(container, out var list) && list.FirstOrDefault(o => Same(o.Name, table)) is { } hit
                ? (container, hit) : null;

        if (DefaultContainer is not null && Objects.TryGetValue(DefaultContainer, out var defaults)
            && defaults.FirstOrDefault(o => Same(o.Name, table)) is { } d)
            return (DefaultContainer, d);

        foreach (var (name, objects) in Objects)
            if (objects.FirstOrDefault(o => Same(o.Name, table)) is { } any)
                return (name, any);
        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Per connection+database cache of <see cref="SchemaSnapshot"/>s for the SQL editor (completion and
/// schema-aware highlighting). Raises <see cref="Changed"/> (from a worker thread) when more becomes known.
/// </summary>
public static class SchemaCache
{
    private const int MaxContainers = 60;

    private static readonly ConcurrentDictionary<string, SchemaSnapshot> Cache = new();

    /// <summary>Fired with the connection Id whenever a snapshot gains objects or columns.</summary>
    public static event Action<Guid>? Changed;

    private static string Key(SavedConnection c) => $"{c.Id}|{c.Database}";

    /// <summary>Returns the snapshot for the connection (empty at first), starting the background load once.</summary>
    public static SchemaSnapshot Get(SavedConnection c)
    {
        var created = false;
        var snapshot = Cache.GetOrAdd(Key(c), _ =>
        {
            created = true;
            return new SchemaSnapshot(c);
        });
        if (created)
            _ = Task.Run(() => LoadAsync(snapshot));
        return snapshot;
    }

    /// <summary>The snapshot if one exists, without starting a load (so a disconnect doesn't reconnect).</summary>
    public static SchemaSnapshot? TryGet(SavedConnection c) => Cache.GetValueOrDefault(Key(c));

    /// <summary>Drops every cached database of the connection (tree refresh, disconnect, DDL).</summary>
    public static void Invalidate(Guid connectionId)
    {
        foreach (var key in Cache.Keys.Where(k => k.StartsWith(connectionId + "|", StringComparison.Ordinal)))
            Cache.TryRemove(key, out _);
        Changed?.Invoke(connectionId);
    }

    /// <summary>Loads (once) the columns of a table; completes immediately when cached.</summary>
    public static Task EnsureColumnsAsync(SchemaSnapshot s, string container, string table)
    {
        var key = SchemaSnapshot.ColumnKey(container, table);
        if (s.Columns.ContainsKey(key)) return Task.CompletedTask;
        return s.ColumnLoads.GetOrAdd(key, _ => Task.Run(async () =>
        {
            List<ColumnInfo> cols;
            try { cols = await ProviderRegistry.For(s.Connection).GetColumnsAsync(s.Connection, container, table); }
            catch { cols = new List<ColumnInfo>(); }
            s.Columns[key] = cols;

            var names = new HashSet<string>(s.ColumnNames, StringComparer.OrdinalIgnoreCase);
            names.UnionWith(cols.Select(col => col.Name));
            s.ColumnNames = names;
            Changed?.Invoke(s.Connection.Id);
        }));
    }

    private static async Task LoadAsync(SchemaSnapshot s)
    {
        try
        {
            var provider = ProviderRegistry.For(s.Connection);
            if (provider.QueryLanguage == "redis") return; // keys aren't schema; never SCAN a whole keyspace for completion

            var containers = await provider.GetContainersAsync(s.Connection);
            var objects = new Dictionary<string, List<DbObjectInfo>>(StringComparer.OrdinalIgnoreCase);

            s.DefaultContainer = PickDefault(s.Connection, containers);
            // The default container first, so it's ready soonest; then a bounded number of others.
            var order = containers.OrderByDescending(x => x == s.DefaultContainer).Take(MaxContainers);
            foreach (var container in order)
            {
                try { objects[container] = await provider.GetObjectsAsync(s.Connection, container); }
                catch { objects[container] = new List<DbObjectInfo>(); }
            }

            s.Containers = containers;
            s.Objects = objects;
            s.ObjectNames = new HashSet<string>(objects.Values.SelectMany(l => l.Select(o => o.Name)), StringComparer.OrdinalIgnoreCase);
            s.IsLoaded = true;
            Changed?.Invoke(s.Connection.Id);

            // Warm the columns of a few default-container tables so highlighting knows common columns.
            if (s.DefaultContainer is not null && objects.TryGetValue(s.DefaultContainer, out var defaults))
                foreach (var o in defaults.Take(30))
                    await EnsureColumnsAsync(s, s.DefaultContainer, o.Name);
        }
        catch
        {
            s.IsLoaded = true; // unreachable server etc.: completion falls back to keywords
        }
    }

    /// <summary>The container unqualified names resolve to, by each engine's convention.</summary>
    private static string? PickDefault(SavedConnection c, List<string> containers)
    {
        string? Find(string name) => containers.FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return Find(c.Database)               // MySQL/MariaDB/ClickHouse/Mongo: containers are databases
               ?? Find("public")              // PostgreSQL/DuckDB
               ?? Find("dbo")                 // SQL Server
               ?? Find("main")                // SQLite/DuckDB
               ?? Find(c.Username)            // Oracle: the user's own schema
               ?? Find("default")             // ClickHouse
               ?? containers.FirstOrDefault();
    }
}
