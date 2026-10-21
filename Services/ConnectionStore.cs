using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Models;

namespace Nexus.Services;

/// <summary>
/// Loads and persists the list of <see cref="SavedConnection"/> to
/// ~/.config/Nexus/connections.json. Plain-text passwords never touch disk: callers pass the
/// clear password to <see cref="Save"/> and it is encrypted before writing.
/// </summary>
public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<SavedConnection> Load()
    {
        try
        {
            var path = AppPaths.ConnectionsFile;
            if (!File.Exists(path)) return new List<SavedConnection>();

            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<SavedConnection>>(json, JsonOptions);
            return list ?? new List<SavedConnection>();
        }
        catch
        {
            return new List<SavedConnection>();
        }
    }

    /// <summary>Persists the full connection list atomically (write-temp-then-rename).</summary>
    public void SaveAll(IEnumerable<SavedConnection> connections)
    {
        var list = connections.ToList();
        var json = JsonSerializer.Serialize(list, JsonOptions);

        var path = AppPaths.ConnectionsFile;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Returns the decrypted password for a saved connection.</summary>
    public static string GetPassword(SavedConnection connection) =>
        CryptoService.Decrypt(connection.EncryptedPassword);

    /// <summary>Sets and encrypts the password on a connection object (does not persist).</summary>
    public static void SetPassword(SavedConnection connection, string clearPassword) =>
        connection.EncryptedPassword = CryptoService.Encrypt(clearPassword);
}
