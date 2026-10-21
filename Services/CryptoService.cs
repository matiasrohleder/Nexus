using System;
using System.IO;
using System.Security.Cryptography;

namespace Nexus.Services;

/// <summary>
/// AES-256-GCM with a random 32-byte key kept in a file (mode 0600 on Unix). Used only by the
/// fallback vault (machines without an OS keyring) and to migrate pre-0.2 passwords, which were
/// stored inside connections.json with <see cref="AppPaths.LegacyKeyFile"/>.
/// </summary>
public sealed class CryptoService
{
    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16;   // AES-GCM standard tag

    private readonly string _keyPath;
    private readonly bool _createKey;
    private byte[]? _key;

    /// <summary>Reads the pre-0.2 key; never creates one.</summary>
    public static CryptoService Legacy => new(AppPaths.LegacyKeyFile, createKey: false);

    public CryptoService(string keyPath, bool createKey = true)
    {
        _keyPath = keyPath;
        _createKey = createKey;
    }

    private byte[]? Key => _key ??= LoadOrCreateKey();

    private byte[]? LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == 32) return existing;
        }
        if (!_createKey) return null;

        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        WriteRestricted(_keyPath, key);
        return key;
    }

    /// <summary>Writes a file readable only by the current user (0600 from creation on Unix).</summary>
    public static void WriteRestricted(string path, byte[] contents)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, contents);
            return;
        }
        using var fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        fs.Write(contents);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        var key = Key ?? throw new InvalidOperationException("No encryption key available.");

        var plain = System.Text.Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(plain);

        // layout: nonce | tag | cipher
        var blob = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(blob);
    }

    public string Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted) || Key is not { } key) return string.Empty;

        try
        {
            var blob = Convert.FromBase64String(encrypted);
            if (blob.Length < NonceSize + TagSize) return string.Empty;

            var nonce = blob.AsSpan(0, NonceSize);
            var tag = blob.AsSpan(NonceSize, TagSize);
            var cipher = blob.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            var text = System.Text.Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return text;
        }
        catch
        {
            // Wrong key or tampered blob — fail closed with empty password.
            return string.Empty;
        }
    }
}
