using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nexus.Models;

namespace Nexus.Services;

/// <summary>
/// Which saved connections currently hold live server connections (driver pools, Mongo clusters).
/// Providers report every profile they open — including database-pinned clones, which get their
/// own pools — so <see cref="Providers.IDatabaseProvider.DisconnectAsync"/> can close them all.
/// Nothing is opened at startup: a connection becomes "connected" on first real use.
/// </summary>
public static class ConnectionStatus
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, SavedConnection>> Opened = new();

    /// <summary>Raised (on a worker thread) when a connection flips between connected/disconnected.</summary>
    public static event Action<Guid>? Changed;

    public static bool IsConnected(Guid id) => Opened.TryGetValue(id, out var map) && !map.IsEmpty;

    /// <summary>Records that a live connection was opened for this profile.</summary>
    public static void MarkOpened(SavedConnection c)
    {
        var map = Opened.GetOrAdd(c.Id, _ => new ConcurrentDictionary<string, SavedConnection>());
        var wasEmpty = map.IsEmpty;
        map[$"{c.Database}\n{c.FilePath}"] = c.Clone();
        if (wasEmpty) Changed?.Invoke(c.Id);
    }

    /// <summary>Forgets a connection and returns every profile it opened (for the provider to close).</summary>
    public static IReadOnlyList<SavedConnection> Forget(Guid id)
    {
        if (!Opened.TryRemove(id, out var map)) return Array.Empty<SavedConnection>();
        Changed?.Invoke(id);
        return map.Values.ToList();
    }
}
