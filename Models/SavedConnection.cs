using System;
using System.Text.Json.Serialization;

namespace Nexus.Models;

/// <summary>
/// A persisted connection profile (connections.json). It never holds the password: that lives
/// in the OS keyring under this profile's <see cref="Id"/> (see <see cref="Services.SecretStore"/>),
/// so the JSON file can be copied to another machine without leaking credentials.
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

    /// <summary>
    /// Pre-0.2 files kept an AES-encrypted password here. Read only so <see cref="Services.ConnectionStore"/>
    /// can move it into the OS keyring; never written again.
    /// </summary>
    [JsonPropertyName("EncryptedPassword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyEncryptedPassword { get; set; }

    /// <summary>
    /// A password typed in the editor but not yet saved. In memory only; <see cref="Services.ConnectionStore.SaveAll"/>
    /// moves it into the keyring. Lets "Test Connection" work before (or without) saving.
    /// </summary>
    [JsonIgnore]
    public string? PendingPassword { get; set; }

    /// <summary>Npgsql SslMode name: Disable, Prefer, Require, VerifyCA, VerifyFull.</summary>
    public string SslMode { get; set; } = "Prefer";

    /// <summary>Optional accent color (hex) shown in the sidebar to distinguish environments.</summary>
    public string Color { get; set; } = "#3B82F6";

    /// <summary>Explorer folder this connection is grouped under (null = top level).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Folder { get; set; }

    public SavedConnection Clone() => (SavedConnection)MemberwiseClone();

    // Identity is the profile Id: a database-pinned clone (see the object explorer) still "is" its
    // saved profile, so pickers and list lookups match it.
    public override bool Equals(object? obj) => obj is SavedConnection other && other.Id == Id;
    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => Name;
}
