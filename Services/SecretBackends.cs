using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Services;

// Native OS credential stores, called directly through P/Invoke (no helper processes, so a
// password never appears on a command line or in another process's environment).

/// <summary>Windows Credential Manager (generic credentials, DPAPI-encrypted per user).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialBackend : ISecretBackend
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public string Name => "Windows Credential Manager";

    private static string Target(Guid id) => $"Nexus/connection/{id}";

    public string? Read(Guid id)
    {
        if (!CredRead(Target(id), CredTypeGeneric, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound) return null;
            throw new CryptographicException($"CredRead failed (Win32 error {err}).");
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return string.Empty;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var text = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return text;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Write(Guid id, string label, string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(id),
                Comment = $"Nexus — {label}",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "Nexus"
            };
            if (!CredWrite(ref cred, 0))
                throw new CryptographicException($"CredWrite failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(Guid id)
    {
        if (!CredDelete(Target(id), CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
            throw new CryptographicException($"CredDelete failed (Win32 error {Marshal.GetLastWin32Error()}).");
    }
}

/// <summary>macOS login Keychain (generic passwords, service "Nexus", account = connection id).</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainBackend : ISecretBackend
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;

    private static readonly byte[] Service = Encoding.UTF8.GetBytes("Nexus");

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain,
        uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData, IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray,
        uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList,
        uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    public string Name => "macOS Keychain";

    private static byte[] Account(Guid id) => Encoding.UTF8.GetBytes(id.ToString());

    /// <summary>Finds the item; returns its ref (caller releases) and optionally the password.</summary>
    private static IntPtr Find(Guid id, out string? password)
    {
        password = null;
        var account = Account(id);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
            (uint)account.Length, account, out var length, out var data, out var item);
        if (status == ErrSecItemNotFound) return IntPtr.Zero;
        if (status != 0) throw new CryptographicException($"Keychain lookup failed (OSStatus {status}).");

        try
        {
            var bytes = new byte[length];
            if (length > 0) Marshal.Copy(data, bytes, 0, (int)length);
            password = Encoding.UTF8.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
        }
        return item;
    }

    public string? Read(Guid id)
    {
        var item = Find(id, out var password);
        if (item != IntPtr.Zero) CFRelease(item);
        return password;
    }

    public void Write(Guid id, string label, string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var item = Find(id, out _);
            int status;
            if (item != IntPtr.Zero)
            {
                status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes);
                CFRelease(item);
            }
            else
            {
                var account = Account(id);
                status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
                    (uint)account.Length, account, (uint)bytes.Length, bytes, IntPtr.Zero);
            }
            if (status != 0) throw new CryptographicException($"Keychain write failed (OSStatus {status}).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete(Guid id)
    {
        var item = Find(id, out _);
        if (item == IntPtr.Zero) return;
        try
        {
            var status = SecKeychainItemDelete(item);
            if (status != 0) throw new CryptographicException($"Keychain delete failed (OSStatus {status}).");
        }
        finally
        {
            CFRelease(item);
        }
    }
}

/// <summary>
/// Linux Secret Service (GNOME Keyring, KWallet ≥ 5.97, KeePassXC…) through libsecret's
/// non-variadic *v_sync API. Items carry the schema "app.nexus.Connection" and the attribute
/// connection=&lt;id&gt;, and show up as "Nexus — &lt;name&gt;" in Seahorse / KWalletManager.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSecretServiceBackend : ISecretBackend
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGlib = "libglib-2.0.so.0";
    private const string Attribute = "connection";

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public IntPtr Message;
    }

    [DllImport(LibGlib)] private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);
    [DllImport(LibGlib)] private static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(LibGlib)] private static extern void g_hash_table_unref(IntPtr table);
    [DllImport(LibGlib)] private static extern void g_error_free(IntPtr error);

    [DllImport(LibSecret)]
    private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection,
        IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes,
        IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    private static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes,
        IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)] private static extern void secret_password_free(IntPtr password);

    private readonly IntPtr _schema;
    private readonly IntPtr _strHash;
    private readonly IntPtr _strEqual;

    private LinuxSecretServiceBackend()
    {
        var glib = NativeLibrary.Load(LibGlib);
        _strHash = NativeLibrary.GetExport(glib, "g_str_hash");
        _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");
        _schema = BuildSchema();
    }

    public string Name => "system keyring (Secret Service)";

    /// <summary>Returns a backend if libsecret is installed and a Secret Service answers on D-Bus.</summary>
    public static LinuxSecretServiceBackend? TryCreate()
    {
        if (!NativeLibrary.TryLoad(LibSecret, out _) || !NativeLibrary.TryLoad(LibGlib, out _))
            return null;
        try
        {
            var backend = new LinuxSecretServiceBackend();
            backend.Read(Guid.Empty); // probe: throws when no Secret Service is reachable
            return backend;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// SecretSchema laid out by hand (64-bit): name ptr, flags (+pad), 32 × {name ptr, type (+pad)},
    /// reserved int (+pad), 7 reserved pointers. Allocated once for the process lifetime.
    /// </summary>
    private static IntPtr BuildSchema()
    {
        const int size = 8 + 8 + 32 * 16 + 8 + 7 * 8;
        var schema = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(schema, i, 0);
        Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8("app.nexus.Connection"));
        Marshal.WriteInt32(schema, 8, 0);                                                  // SECRET_SCHEMA_NONE
        Marshal.WriteIntPtr(schema, 16, Marshal.StringToCoTaskMemUTF8(Attribute));
        Marshal.WriteInt32(schema, 24, 0);                                                 // ATTRIBUTE_STRING
        return schema;
    }

    private IntPtr Attributes(Guid id, out IntPtr key, out IntPtr value)
    {
        var table = g_hash_table_new(_strHash, _strEqual);
        key = Marshal.StringToCoTaskMemUTF8(Attribute);
        value = Marshal.StringToCoTaskMemUTF8(id.ToString());
        g_hash_table_insert(table, key, value);
        return table;
    }

    private static void ThrowIfError(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        var e = Marshal.PtrToStructure<GError>(error);
        var message = Marshal.PtrToStringUTF8(e.Message) ?? "unknown error";
        g_error_free(error);
        throw new CryptographicException($"Secret Service: {message}");
    }

    public string? Read(Guid id)
    {
        var table = Attributes(id, out var k, out var v);
        try
        {
            var result = secret_password_lookupv_sync(_schema, table, IntPtr.Zero, out var error);
            ThrowIfError(error);
            if (result == IntPtr.Zero) return null;
            var text = Marshal.PtrToStringUTF8(result);
            secret_password_free(result); // wipes the buffer
            return text;
        }
        finally
        {
            g_hash_table_unref(table);
            Marshal.FreeCoTaskMem(k);
            Marshal.FreeCoTaskMem(v);
        }
    }

    public void Write(Guid id, string label, string password)
    {
        var table = Attributes(id, out var k, out var v);
        var labelPtr = Marshal.StringToCoTaskMemUTF8($"Nexus — {label}");
        var bytes = Encoding.UTF8.GetBytes(password);
        var pwPtr = Marshal.AllocCoTaskMem(bytes.Length + 1);
        try
        {
            Marshal.Copy(bytes, 0, pwPtr, bytes.Length);
            Marshal.WriteByte(pwPtr, bytes.Length, 0);
            secret_password_storev_sync(_schema, table, IntPtr.Zero, labelPtr, pwPtr, IntPtr.Zero, out var error);
            ThrowIfError(error);
        }
        finally
        {
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(pwPtr, i, 0);
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeCoTaskMem(pwPtr);
            Marshal.FreeCoTaskMem(labelPtr);
            g_hash_table_unref(table);
            Marshal.FreeCoTaskMem(k);
            Marshal.FreeCoTaskMem(v);
        }
    }

    public void Delete(Guid id)
    {
        var table = Attributes(id, out var k, out var v);
        try
        {
            secret_password_clearv_sync(_schema, table, IntPtr.Zero, out var error);
            ThrowIfError(error);
        }
        finally
        {
            g_hash_table_unref(table);
            Marshal.FreeCoTaskMem(k);
            Marshal.FreeCoTaskMem(v);
        }
    }
}
