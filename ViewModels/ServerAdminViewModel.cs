using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Models;
using Nexus.Providers;

namespace Nexus.ViewModels;

/// <summary>
/// "Server" tab for one connection: active sessions (with kill), users/roles, table sizes
/// and server variables. Sections appear according to the engine's capabilities.
/// </summary>
public partial class ServerAdminViewModel : DocumentViewModel
{
    public SavedConnection Connection { get; }
    private IDatabaseProvider Provider => ProviderRegistry.For(Connection);

    public ObservableCollection<SessionInfo> Sessions { get; } = new();
    public ObservableCollection<UserInfo> Users { get; } = new();
    public ObservableCollection<TableStats> TableStats { get; } = new();
    public ObservableCollection<ServerVariable> Variables { get; } = new();
    public ObservableCollection<string> Containers { get; } = new();

    [ObservableProperty] private SessionInfo? _selectedSession;
    [ObservableProperty] private string? _selectedContainer;
    [ObservableProperty] private string _variableFilter = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";

    public bool SupportsKill => Provider.Capabilities.SupportsKillSession;
    public bool SupportsUsers => Provider.Capabilities.SupportsUsers;
    public bool SupportsSessions => Provider.Capabilities.SupportsServerActivity || SupportsKill;

    private ServerVariable[] _allVariables = Array.Empty<ServerVariable>();

    public override string Icon => "server";

    public ServerAdminViewModel(SavedConnection connection)
    {
        Connection = connection;
        Title = connection.Name;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Loading…";
        try
        {
            if (SupportsSessions)
            {
                var sessions = await Provider.GetSessionsAsync(Connection);
                Sessions.Clear();
                foreach (var s in sessions) Sessions.Add(s);
            }

            if (SupportsUsers)
            {
                var users = await Provider.GetUsersAsync(Connection);
                Users.Clear();
                foreach (var u in users) Users.Add(u);
            }

            var containers = await Provider.GetContainersAsync(Connection);
            Containers.Clear();
            foreach (var c in containers) Containers.Add(c);
            SelectedContainer ??= Containers.FirstOrDefault();
            await LoadTableStatsAsync();

            _allVariables = (await Provider.GetServerVariablesAsync(Connection)).ToArray();
            ApplyVariableFilter();

            StatusText = $"Refreshed {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedContainerChanged(string? value) => _ = LoadTableStatsAsync();
    partial void OnVariableFilterChanged(string value) => ApplyVariableFilter();

    private async Task LoadTableStatsAsync()
    {
        if (SelectedContainer is null) return;
        try
        {
            var stats = await Provider.GetTableStatsAsync(Connection, SelectedContainer);
            TableStats.Clear();
            foreach (var s in stats) TableStats.Add(s);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void ApplyVariableFilter()
    {
        Variables.Clear();
        var filter = VariableFilter.Trim();
        foreach (var v in _allVariables)
            if (filter.Length == 0 || v.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Variables.Add(v);
    }

    // ---- backup -------------------------------------------------------------------------

    public bool BackupAvailable => Services.BackupService.Describe(Connection).Available;
    public string BackupHint => Services.BackupService.Describe(Connection).Hint;

    [ObservableProperty] private string _backupDestination = "";
    [ObservableProperty] private string _backupLog = "";
    [ObservableProperty] private bool _backupRunning;

    /// <summary>Wired by the view: asks for a destination path for the backup.</summary>
    public Func<string, Task<string?>>? PickBackupPath { get; set; }

    [RelayCommand]
    private async Task BrowseBackupAsync()
    {
        if (PickBackupPath is null) return;
        var suggested = $"{Connection.Name.Replace(' ', '_')}-{DateTime.Now:yyyyMMdd-HHmm}" +
                        (Connection.Provider is "mongodb" or "duckdb" ? "" : ".sql");
        var path = await PickBackupPath(suggested);
        if (path is not null) BackupDestination = path;
    }

    [RelayCommand]
    private async Task RunBackupAsync()
    {
        if (BackupRunning || string.IsNullOrWhiteSpace(BackupDestination)) return;
        BackupRunning = true;
        BackupLog = "";
        try
        {
            var lines = new System.Collections.Generic.List<string>();
            await Services.BackupService.BackupAsync(Connection, BackupDestination.Trim(), line =>
            {
                lines.Add(line);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => BackupLog = string.Join('\n', lines));
            });
        }
        finally
        {
            BackupRunning = false;
        }
    }

    [RelayCommand]
    private async Task KillSessionAsync()
    {
        if (SelectedSession is null || !SupportsKill) return;
        try
        {
            await Provider.KillSessionAsync(Connection, SelectedSession.Id);
            StatusText = $"Session {SelectedSession.Id} terminated.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Kill failed: {ex.Message}";
        }
    }
}
