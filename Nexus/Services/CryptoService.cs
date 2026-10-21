using System;
using System.IO;
using System.Security.Cryptography;

namespace Nexus.Services;

/// <summary>
/// Encrypts/decrypts saved-connection passwords with AES-256-GCM. The key is a random
/// 32-byte value stored once at ~/.config/Nexus/key.bin with 0600 permissions, so passwords
/// are never written to disk in clear text. This is local-desktop protection (parity with how
/// GUI DB tools store credentials) — not a substitute for a system keyring.
/// </summary>
public static class CryptoService
{
    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16;   // AES-GCM standard tag

    private static readonly Lazy<byte[]> Key = new(LoadOrCreateKey);

    private static byte[] LoadOrCreateKey()
    {
        var path = AppPaths.KeyFile;
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32) return existing;
        }

        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, key);
        TryRestrictPermissions(path);
        return key;
    }

    private static void TryRestrictPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best effort */ }
    }

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        var plain = System.Text.Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(Key.Value, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        // layout: nonce | tag | cipher
        var blob = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(blob);
    }

    public static string Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return string.Empty;

        try
        {
            var blob = Convert.FromBase64String(encrypted);
            if (blob.Length < NonceSize + TagSize) return string.Empty;

            var nonce = blob.AsSpan(0, NonceSize);
            var tag = blob.AsSpan(NonceSize, TagSize);
            var cipher = blob.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(Key.Value, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Wrong key or tampered blob — fail closed with empty password.
            return string.Empty;
        }
    }
}
