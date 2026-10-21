using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Models;

namespace Nexus.Services;

/// <summary>
/// Loads and persists the list of <see cref="SavedConnection"/> to
/// ~/.config/Nexus/connections.json. The file holds no secrets: passwords go to the OS keyring
/// through <see cref="SecretStore"/>, so the JSON can be exported/copied to another machine.
/// </summary>
public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<SavedConnection> Load()
    {
        var list = ReadFile(AppPaths.ConnectionsFile);
        if (MigrateLegacyPasswords(list))
            WriteFile(AppPaths.ConnectionsFile, list);
        return list;
    }

    /// <summary>
    /// Persists the full connection list atomically (write-temp-then-rename), first committing any
    /// pending password edits to the keyring.
    /// </summary>
    public void SaveAll(IEnumerable<SavedConnection> connections)
    {
        var list = connections.ToList();
        foreach (var c in list.Where(c => c.PendingPassword is not null))
        {
            SecretStore.Set(c.Id, c.Name, c.PendingPassword!);
            c.PendingPassword = null;
        }
        WriteFile(AppPaths.ConnectionsFile, list);
    }

    /// <summary>Writes connection profiles (never passwords) to a user-chosen file.</summary>
    public static void Export(IEnumerable<SavedConnection> connections, string path) =>
        WriteFile(path, connections.ToList());

    /// <summary>Reads profiles from an exported file. Throws on unreadable/invalid JSON.</summary>
    public static List<SavedConnection> ReadExport(string path)
    {
        var list = JsonSerializer.Deserialize<List<SavedConnection>>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException("The file does not contain a connection list.");
        // Blobs from another machine can't be decrypted here; never carry them along.
        foreach (var c in list) c.LegacyEncryptedPassword = null;
        return list;
    }

    /// <summary>Returns the password for a connection: an unsaved edit, else the keyring entry.</summary>
    public static string GetPassword(SavedConnection connection) =>
        connection.PendingPassword ?? SecretStore.Get(connection.Id);

    /// <summary>Sets the password in memory; it reaches the keyring when the list is saved.</summary>
    public static void SetPassword(SavedConnection connection, string clearPassword) =>
        connection.PendingPassword = clearPassword;

    /// <summary>Removes a deleted connection's password from the keyring.</summary>
    public static void ForgetPassword(SavedConnection connection) => SecretStore.Delete(connection.Id);

    private static List<SavedConnection> ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<SavedConnection>();
            return JsonSerializer.Deserialize<List<SavedConnection>>(File.ReadAllText(path), JsonOptions)
                   ?? new List<SavedConnection>();
        }
        catch
        {
            return new List<SavedConnection>();
        }
    }

    private static void WriteFile(string path, List<SavedConnection> list)
    {
        var json = JsonSerializer.Serialize(list, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Moves pre-0.2 passwords (AES blobs inside connections.json) into the keyring, then deletes
    /// the old key file once no blob references it. Returns true if the file must be rewritten.
    /// </summary>
    private static bool MigrateLegacyPasswords(List<SavedConnection> list)
    {
        var changed = false;
        foreach (var c in list.Where(c => c.LegacyEncryptedPassword is not null))
        {
            try
            {
                var password = CryptoService.Legacy.Decrypt(c.LegacyEncryptedPassword!);
                if (password.Length > 0) SecretStore.Set(c.Id, c.Name, password);
                c.LegacyEncryptedPassword = null;
                changed = true;
            }
            catch
            {
                // Keep the blob so nothing is lost; migration is retried on next start.
            }
        }

        // Only retire the old key right after this file's last blob was migrated with it — never
        // just because the current file has no blobs (the key may still belong to a backup).
        if (changed && list.All(c => c.LegacyEncryptedPassword is null) && File.Exists(AppPaths.LegacyKeyFile))
        {
            try { File.Delete(AppPaths.LegacyKeyFile); } catch { /* best effort */ }
        }
        return changed;
    }
}
