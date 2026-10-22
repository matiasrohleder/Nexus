using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Nexus.Services;

/// <summary>A place passwords can live. Implementations must never write clear text to disk.</summary>
internal interface ISecretBackend
{
    string Name { get; }
    string? Read(Guid id);
    void Write(Guid id, string label, string password);
    void Delete(Guid id);
}

/// <summary>
/// Connection passwords, kept out of connections.json and stored in the operating system's
/// credential store instead:
///   Windows → Credential Manager (encrypted with DPAPI, bound to the user's logon)
///   macOS   → login Keychain
///   Linux   → Secret Service (GNOME Keyring, KWallet, KeePassXC…) via libsecret
/// Entries are keyed by the connection's Id, so database-pinned clones share them. Only when no
/// keyring is reachable (e.g. a bare Linux session without a Secret Service) do passwords go to
/// an AES-256-GCM vault under <see cref="AppPaths.DataDir"/> — still never into connections.json.
/// </summary>
public static class SecretStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, string> Cache = new();
    private static ISecretBackend? _primary;
    private static VaultBackend? _vault;
    private static bool _initialized;

    /// <summary>Human-readable name of where passwords are stored (shown in the connection dialog).</summary>
    public static string LocationName
    {
        get
        {
            lock (Gate)
            {
                Initialize();
                return _primary?.Name ?? Vault.Name;
            }
        }
    }

    /// <summary>True when passwords go to an OS keyring rather than the fallback vault.</summary>
    public static bool UsesSystemKeyring
    {
        get
        {
            lock (Gate)
            {
                Initialize();
                return _primary is not null and not MemoryBackend;
            }
        }
    }

    /// <summary>Self-tests: keep secrets in memory so test runs never touch the user's keyring.</summary>
    public static void UseEphemeral()
    {
        lock (Gate)
        {
            _primary = new MemoryBackend();
            _initialized = true;
            Cache.Clear();
        }
    }

    /// <summary>Self-tests: forget cached values so the next Get really hits the backend.</summary>
    public static void ClearCache()
    {
        lock (Gate) Cache.Clear();
    }

    public static string Get(Guid id)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(id, out var cached)) return cached;
            Initialize();

            string? value = null;
            try { value = _primary?.Read(id); }
            catch { /* keyring temporarily unavailable — try the vault */ }
            value ??= VaultExists ? Vault.Read(id) : null;

            Cache[id] = value ?? string.Empty;
            return Cache[id];
        }
    }

    /// <summary>Stores (or, for an empty password, removes) the secret for a connection.</summary>
    public static void Set(Guid id, string label, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            Delete(id);
            return;
        }

        lock (Gate)
        {
            Initialize();
            var stored = false;
            if (_primary is not null)
            {
                try
                {
                    _primary.Write(id, label, password);
                    stored = true;
                    if (VaultExists) Vault.Delete(id);
                }
                catch { /* fall back to the vault below */ }
            }
            if (!stored) Vault.Write(id, label, password);
            Cache[id] = password;
        }
    }

    public static void Delete(Guid id)
    {
        lock (Gate)
        {
            Initialize();
            try { _primary?.Delete(id); } catch { /* best effort */ }
            if (VaultExists) Vault.Delete(id);
            Cache.Remove(id);
        }
    }

    private static VaultBackend Vault => _vault ??= new VaultBackend();
    private static bool VaultExists => _vault is not null || File.Exists(VaultBackend.VaultPath);

    private static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (OperatingSystem.IsWindows()) _primary = new WindowsCredentialBackend();
            else if (OperatingSystem.IsMacOS()) _primary = new MacKeychainBackend();
            else if (OperatingSystem.IsLinux() && LinuxSecretServiceBackend.TryCreate() is { } linux) _primary = linux;
        }
        catch
        {
            _primary = null;
        }
    }
}

/// <summary>Process-lifetime store for self-tests.</summary>
internal sealed class MemoryBackend : ISecretBackend
{
    private readonly Dictionary<Guid, string> _items = new();
    public string Name => "memory (test)";
    public string? Read(Guid id) => _items.TryGetValue(id, out var v) ? v : null;
    public void Write(Guid id, string label, string password) => _items[id] = password;
    public void Delete(Guid id) => _items.Remove(id);
}

/// <summary>
/// Fallback when the OS has no reachable keyring: an AES-256-GCM encrypted map in DataDir, with
/// its key in a separate 0600 file. Both live outside the config dir, so copying
/// connections.json elsewhere never carries passwords along.
/// </summary>
internal sealed class VaultBackend : ISecretBackend
{
    public static string VaultPath => Path.Combine(AppPaths.DataDir, "vault", "secrets.vault");
    private static string KeyPath => Path.Combine(AppPaths.DataDir, "vault", "vault.key");

    private readonly CryptoService _crypto = new(KeyPath);

    public string Name => "Nexus encrypted vault (no system keyring found)";

    public string? Read(Guid id) =>
        Load().TryGetValue(id.ToString(), out var blob) ? _crypto.Decrypt(blob) : null;

    public void Write(Guid id, string label, string password)
    {
        var map = Load();
        map[id.ToString()] = _crypto.Encrypt(password);
        Save(map);
    }

    public void Delete(Guid id)
    {
        var map = Load();
        if (map.Remove(id.ToString())) Save(map);
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(VaultPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(VaultPath)) ?? new();
        }
        catch { /* unreadable vault → treat as empty */ }
        return new();
    }

    private static void Save(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
        var tmp = VaultPath + ".tmp";
        CryptoService.WriteRestricted(tmp, JsonSerializer.SerializeToUtf8Bytes(map));
        File.Move(tmp, VaultPath, overwrite: true);
    }
}
