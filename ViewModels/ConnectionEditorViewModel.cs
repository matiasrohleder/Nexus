using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Models;
using Nexus.Providers;
using Nexus.Services;

namespace Nexus.ViewModels;

/// <summary>Backs the Add/Edit Connection dialog. Works on a copy; the caller commits on Save.</summary>
public partial class ConnectionEditorViewModel : ViewModelBase
{
    private readonly SavedConnection _target;
    private readonly string _originalPassword;
    private bool _applyingDefaults;

    // ---- engine tiles ----
    public IReadOnlyList<IDatabaseProvider> Engines { get; } = ProviderRegistry.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileBased))]
    [NotifyPropertyChangedFor(nameof(IsServerBased))]
    private IDatabaseProvider _selectedEngine;

    public bool IsFileBased => SelectedEngine.Capabilities.IsFileBased;
    public bool IsServerBased => !IsFileBased;

    // ---- fields ----
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _host;
    [ObservableProperty] private int _port;
    [ObservableProperty] private string _database;
    [ObservableProperty] private string _username;
    [ObservableProperty] private string _password;
    [ObservableProperty] private string _filePath;
    [ObservableProperty] private string _sslMode;
    [ObservableProperty] private string _color;

    // ---- paste URL ----
    [ObservableProperty] private string _pasteUrl = string.Empty;
    [ObservableProperty] private string _pasteUrlError = string.Empty;

    // ---- test / state ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestMessage))]
    private string _testMessage = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestIcon))]
    [NotifyPropertyChangedFor(nameof(TestColor))]
    private bool _testSucceeded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestIcon))]
    [NotifyPropertyChangedFor(nameof(TestColor))]
    private bool _isBusy;

    public bool HasTestMessage => !string.IsNullOrEmpty(TestMessage);

    /// <summary>Result banner styling: neutral while connecting, green on success, red on failure.</summary>
    public string TestIcon => IsBusy ? "clock" : TestSucceeded ? "check" : "error";
    public string TestColor => IsBusy ? "#9CA3AF" : TestSucceeded ? "#4EC977" : "#F48771";

    public string DialogTitle { get; }

    public IReadOnlyList<string> SslModes { get; } =
        new[] { "Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull" };

    /// <summary>Predefined accent colors (click-to-pick swatches); the current one is ringed.</summary>
    public IReadOnlyList<ColorSwatch> ColorSwatches { get; } =
        new[] { "#3B82F6", "#22C55E", "#EF4444", "#F59E0B", "#A855F7", "#14B8A6", "#64748B" }
            .Select(h => new ColorSwatch(h)).ToArray();

    partial void OnColorChanged(string value)
    {
        foreach (var s in ColorSwatches)
            s.IsSelected = string.Equals(s.Hex, value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Wired by the view: opens a file picker and returns the chosen path (file-based engines).</summary>
    public Func<Task<string?>>? PickFile { get; set; }

    public ConnectionEditorViewModel(SavedConnection? existing)
    {
        _target = existing?.Clone() ?? new SavedConnection();
        DialogTitle = existing is null ? "New Connection" : "Edit Connection";

        _selectedEngine = ProviderRegistry.All.FirstOrDefault(p => p.Id == _target.Provider)
                          ?? ProviderRegistry.All[0];

        _name = existing is null ? string.Empty : _target.Name;
        _host = _target.Host;
        _port = existing is null ? _selectedEngine.DefaultPort : _target.Port;
        _database = existing is null ? _selectedEngine.DefaultDatabase : _target.Database;
        _username = existing is null ? _selectedEngine.DefaultUsername : _target.Username;
        _password = _originalPassword = ConnectionStore.GetPassword(_target);
        _filePath = _target.FilePath;
        _sslMode = _target.SslMode;
        _color = _target.Color;
        OnColorChanged(_color);
    }

    /// <summary>Switching engine re-applies that engine's defaults for port/user/database.</summary>
    partial void OnSelectedEngineChanged(IDatabaseProvider value)
    {
        if (_applyingDefaults) return;
        _applyingDefaults = true;
        Port = value.DefaultPort;
        Username = value.DefaultUsername;
        Database = value.DefaultDatabase;
        TestMessage = string.Empty;
        _applyingDefaults = false;
    }

    /// <summary>Where saved passwords go (OS keyring name), shown under the password field.</summary>
    public string PasswordStorageText { get; } = $"Stored securely in {SecretStore.LocationName} — never in connections.json";

    /// <summary>
    /// Applies the edited fields back onto a SavedConnection. A changed password is only held in
    /// memory here; it reaches the keyring when the caller saves the connection list.
    /// </summary>
    public SavedConnection Commit()
    {
        _target.Provider = SelectedEngine.Id;
        _target.Name = string.IsNullOrWhiteSpace(Name) ? AutoName() : Name.Trim();
        _target.Host = Host.Trim();
        _target.Port = Port;
        _target.Database = Database.Trim();
        _target.Username = Username.Trim();
        _target.FilePath = FilePath.Trim();
        _target.SslMode = SslMode;
        _target.Color = Color;
        if (Password != _originalPassword || _target.PendingPassword is not null)
            ConnectionStore.SetPassword(_target, Password);
        return _target;
    }

    private string AutoName() =>
        IsFileBased
            ? System.IO.Path.GetFileNameWithoutExtension(FilePath) is { Length: > 0 } f ? f : SelectedEngine.DisplayName
            : $"{Host}/{Database}";

    [RelayCommand]
    private void SelectColor(string? swatch)
    {
        if (!string.IsNullOrEmpty(swatch)) Color = swatch;
    }

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        if (PickFile is null) return;
        var path = await PickFile();
        if (!string.IsNullOrEmpty(path)) FilePath = path;
    }

    /// <summary>
    /// Parses a pasted URL (postgres://user:pass@host:5432/db, mysql://…, mongodb://…, redis://…)
    /// or an ADO.NET-style connection string (Host=…;Port=…;Database=…) and fills the form.
    /// </summary>
    [RelayCommand]
    private void ApplyPastedUrl()
    {
        PasteUrlError = string.Empty;
        var raw = PasteUrl.Trim();
        if (raw.Length == 0) return;

        try
        {
            if (raw.Contains("://"))
                ParseUri(raw);
            else if (raw.Contains('='))
                ParseKeyValue(raw);
            else
            {
                PasteUrlError = "Not a recognized URL or connection string.";
                return;
            }
            PasteUrl = string.Empty;
            TestMessage = string.Empty;
        }
        catch (Exception ex)
        {
            PasteUrlError = $"Could not parse: {ex.Message}";
        }
    }

    private static readonly Dictionary<string, string> SchemeToProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgres"] = "postgresql", ["postgresql"] = "postgresql",
        ["mysql"] = "mysql", ["mariadb"] = "mariadb",
        ["mssql"] = "sqlserver", ["sqlserver"] = "sqlserver",
        ["oracle"] = "oracle",
        ["clickhouse"] = "clickhouse",
        ["mongodb"] = "mongodb", ["mongodb+srv"] = "mongodb",
        ["redis"] = "redis", ["rediss"] = "redis",
        ["sqlite"] = "sqlite", ["duckdb"] = "duckdb",
    };

    private void ParseUri(string raw)
    {
        var uri = new Uri(raw);

        if (SchemeToProvider.TryGetValue(uri.Scheme, out var providerId))
        {
            var engine = ProviderRegistry.All.FirstOrDefault(p => p.Id == providerId);
            if (engine is not null) SelectedEngine = engine;
        }

        if (SelectedEngine.Capabilities.IsFileBased)
        {
            FilePath = uri.AbsolutePath;
            return;
        }

        if (!string.IsNullOrEmpty(uri.Host)) Host = uri.Host;
        Port = uri.Port > 0 ? uri.Port : SelectedEngine.DefaultPort;

        var db = uri.AbsolutePath.Trim('/');
        if (db.Length > 0) Database = Uri.UnescapeDataString(db);

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1) Password = Uri.UnescapeDataString(parts[1]);
        }
    }

    private void ParseKeyValue(string raw)
    {
        foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            var value = kv[1].Trim();
            switch (key)
            {
                case "host" or "server" or "data source" when !SelectedEngine.Capabilities.IsFileBased:
                    Host = value; break;
                case "data source" or "filename" when SelectedEngine.Capabilities.IsFileBased:
                    FilePath = value; break;
                case "port": if (int.TryParse(value, out var p)) Port = p; break;
                case "database" or "initial catalog": Database = value; break;
                case "username" or "user id" or "uid" or "user": Username = value; break;
                case "password" or "pwd": Password = value; break;
            }
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        IsBusy = true;
        TestMessage = "Connecting…";
        TestSucceeded = false;
        try
        {
            var probe = Commit().Clone();
            var (ok, message) = await ProviderRegistry.For(probe).TestConnectionAsync(probe);
            TestSucceeded = ok;
            TestMessage = message;
        }
        catch (Exception ex)
        {
            TestMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One accent-color choice in the connection dialog.</summary>
public partial class ColorSwatch(string hex) : ObservableObject
{
    public string Hex { get; } = hex;
    [ObservableProperty] private bool _isSelected;
}
