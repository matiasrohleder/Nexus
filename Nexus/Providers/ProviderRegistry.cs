using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Models;

namespace Nexus.Providers;

/// <summary>
/// Central catalog of database engines. Providers are stateless singletons; look them up by
/// the id stored in <see cref="SavedConnection.Provider"/>.
/// </summary>
public static class ProviderRegistry
{
    // Registration order = display order in the connection dialog.
    private static readonly List<IDatabaseProvider> Providers = new()
    {
        new PostgresProvider(),
        new MySqlProvider(),
        new MariaDbProvider(),
        new SqlServerProvider(),
        new OracleProvider(),
        new SqliteProvider(),
        new DuckDbProvider(),
        new ClickHouseProvider(),
        new MongoProvider(),
        new RedisProvider(),
    };

    public static IReadOnlyList<IDatabaseProvider> All => Providers;

    public static IDatabaseProvider Get(string id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Unknown database provider '{id}'.");

    public static IDatabaseProvider For(SavedConnection c) => Get(c.Provider);

    internal static void Register(IDatabaseProvider provider) => Providers.Add(provider);
}
