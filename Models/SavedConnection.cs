using System;

namespace Nexus.Models;

/// <summary>
/// A persisted PostgreSQL connection profile. The password is stored encrypted
/// on disk (see <see cref="Services.CryptoService"/>); it is never serialized in clear text.
/// </summary>
public sealed class SavedConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Provider id from ProviderRegistry ("postgresql", "mysql", …). Old files without it default to PostgreSQL.</summary>
    public string Provider { get; set; } = "postgresql";

    public string Name { get; set; } = "New Connection";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "postgres";
    public string Username { get; set; } = "postgres";

    /// <summary>Database file path for file-based engines (SQLite, DuckDB).</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Encrypted password blob (base64). Set via <see cref="Services.ConnectionStore"/>.</summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>Npgsql SslMode name: Disable, Prefer, Require, VerifyCA, VerifyFull.</summary>
    public string SslMode { get; set; } = "Prefer";

    /// <summary>Optional accent color (hex) shown in the sidebar to distinguish environments.</summary>
    public string Color { get; set; } = "#3B82F6";

    public SavedConnection Clone() => (SavedConnection)MemberwiseClone();

    public override string ToString() => Name;
}
