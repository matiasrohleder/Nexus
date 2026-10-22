using System;
using System.IO;
using Nexus.Models;
using Nexus.Services;

namespace Nexus;

/// <summary>
/// Verifies password storage against the real OS credential store (Credential Manager / Keychain /
/// Secret Service, or the fallback vault) and that connections.json never contains a secret:
///   dotnet run -- --selftest secrets
/// Runs against a throwaway config directory and connection ids, and removes every keyring entry
/// it creates — the user's real connections and keys are never read or touched.
/// </summary>
public static class SelfTestSecrets
{
    private static int _failures;

    public static int Run()
    {
        Console.WriteLine("== Nexus self-test: secrets ==");
        var root = Path.Combine(Path.GetTempPath(), $"nexus-selftest-{Guid.NewGuid():N}");
        AppPaths.RedirectForTests(root);
        Console.WriteLine($"   backend: {SecretStore.LocationName} (system keyring: {SecretStore.UsesSystemKeyring})");

        var id = Guid.NewGuid();
        const string first = "p@ss-wörd-1 ✓";
        const string second = "second-secret";

        // ---- 1. Raw backend round-trip ----
        SecretStore.Set(id, "_nexus_selftest", first);
        SecretStore.ClearCache();
        Check(SecretStore.Get(id) == first, "stored secret reads back (unicode-safe)");

        SecretStore.Set(id, "_nexus_selftest", second);
        SecretStore.ClearCache();
        Check(SecretStore.Get(id) == second, "overwriting an existing secret");

        SecretStore.Delete(id);
        SecretStore.ClearCache();
        Check(SecretStore.Get(id) == string.Empty, "deleted secret is gone");

        // ---- 2. connections.json never carries passwords ----
        var storePath = AppPaths.ConnectionsFile;
        var conn = new SavedConnection { Name = "_nexus_selftest", Host = "example.invalid" };
        const string password = "hunter2-must-not-leak";
        try
        {
            var store = new ConnectionStore();
            ConnectionStore.SetPassword(conn, password);
            store.SaveAll(new[] { conn });
            var json = File.ReadAllText(storePath);
            Check(!json.Contains(password) && !json.Contains("Password"), "connections.json contains no password field/value");

            SecretStore.ClearCache();
            var reloaded = store.Load();
            Check(reloaded.Count == 1 && ConnectionStore.GetPassword(reloaded[0]) == password,
                "reloaded connection resolves its password from the secret store");

            // ---- 3. Pre-0.2 files: encrypted blob in the JSON migrates into the store ----
            {
                var legacyId = Guid.NewGuid();
                var blob = new CryptoService(AppPaths.LegacyKeyFile).Encrypt("legacy-pw");
                File.WriteAllText(storePath,
                    $"[{{\"Id\":\"{legacyId}\",\"Name\":\"_nexus_legacy\",\"EncryptedPassword\":\"{blob}\"}}]");
                var migrated = store.Load();
                var after = File.ReadAllText(storePath);
                SecretStore.ClearCache();
                Check(ConnectionStore.GetPassword(migrated[0]) == "legacy-pw", "legacy password migrated into the secret store");
                Check(!after.Contains("EncryptedPassword"), "legacy blob removed from connections.json");
                Check(!File.Exists(AppPaths.LegacyKeyFile), "legacy key file deleted after migration");
                SecretStore.Delete(legacyId);
            }
        }
        finally
        {
            ConnectionStore.ForgetPassword(conn);
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine(_failures == 0 ? "\n✅ ALL CHECKS PASSED" : $"\n❌ {_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string label)
    {
        Console.WriteLine($"   [{(condition ? "PASS" : "FAIL")}] {label}");
        if (!condition) _failures++;
    }
}
