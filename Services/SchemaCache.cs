using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Models;
using Nexus.Providers;

namespace Nexus.Services;

/// <summary>
/// Per-connection cache of object names (containers, tables/views, columns of recently
/// introspected tables) used by the SQL editor's completion popup.
/// </summary>
public static class SchemaCache
{
    private static readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> Cache = new();
    private static readonly ConcurrentDictionary<Guid, Task> Loading = new();

    public static readonly string[] SqlKeywords =
    {
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "CREATE TABLE", "ALTER TABLE",
        "DROP TABLE", "CREATE INDEX", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN",
        "ON", "AS", "AND", "OR", "NOT", "NULL", "IS NULL", "IS NOT NULL", "IN", "BETWEEN",
        "LIKE", "EXISTS", "UNION", "UNION ALL", "DISTINCT", "COUNT", "SUM", "AVG", "MIN", "MAX",
        "CASE", "WHEN", "THEN", "ELSE", "END", "CAST", "COALESCE", "PRIMARY KEY", "FOREIGN KEY",
        "REFERENCES", "DEFAULT", "CONSTRAINT", "WITH"
    };

    /// <summary>Returns cached schema words for the connection, triggering a background load on first use.</summary>
    public static IReadOnlyList<string> GetWords(SavedConnection c)
    {
        if (Cache.TryGetValue(c.Id, out var words))
            return words;

        // Kick off one background load per connection.
        Loading.GetOrAdd(c.Id, _ => Task.Run(() => LoadAsync(c)));
        return Array.Empty<string>();
    }

    public static void Invalidate(Guid connectionId)
    {
        Cache.TryRemove(connectionId, out _);
        Loading.TryRemove(connectionId, out _);
    }

    private static async Task LoadAsync(SavedConnection c)
    {
        try
        {
            var provider = ProviderRegistry.For(c);
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var containers = await provider.GetContainersAsync(c);
            foreach (var container in containers)
            {
                words.Add(container);
                var objects = await provider.GetObjectsAsync(c, container);
                foreach (var o in objects)
                    words.Add(o.Name);

                // Column names for the first few tables per container (bounded cost).
                foreach (var o in objects.Take(25))
                {
                    var cols = await provider.GetColumnsAsync(c, container, o.Name);
                    foreach (var col in cols)
                        words.Add(col.Name);
                }
            }

            Cache[c.Id] = words.OrderBy(w => w).ToList();
        }
        catch
        {
            Cache[c.Id] = Array.Empty<string>();
        }
    }
}
