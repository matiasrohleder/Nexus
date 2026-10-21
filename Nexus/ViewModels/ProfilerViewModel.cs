using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Models;
using Nexus.Services;

namespace Nexus.ViewModels;

/// <summary>
/// The Profiler pane. Shows every query Nexus runs (always on) and can additionally poll a
/// selected connection's pg_stat_activity to surface queries from *any* client on the server.
/// </summary>
public partial class ProfilerViewModel : ViewModelBase
{
    private readonly DispatcherTimer _pollTimer;
    private readonly HashSet<string> _seenServerKeys = new();

    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private bool _captureServerActivity;
    [ObservableProperty] private SavedConnection? _serverActivityConnection;
    [ObservableProperty] private string _statusText = "Profiler off — enable \"Capture app queries\" to start";

    public ObservableCollection<QueryLogEntry> Entries => QueryProfiler.Instance.Entries;

    /// <summary>Connections available to poll for server activity (bound from the main VM).</summary>
    public ObservableCollection<SavedConnection> Connections { get; } = new();

    public ProfilerViewModel()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += async (_, _) => await PollServerActivityAsync();
    }

    partial void OnIsCapturingChanged(bool value)
    {
        QueryProfiler.Instance.IsCapturing = value;
        UpdateStatus();
    }

    partial void OnCaptureServerActivityChanged(bool value)
    {
        if (value && ServerActivityConnection is not null)
            _pollTimer.Start();
        else
            _pollTimer.Stop();
        UpdateStatus();
    }

    partial void OnServerActivityConnectionChanged(SavedConnection? value)
    {
        if (CaptureServerActivity)
        {
            _seenServerKeys.Clear();
            if (value is not null) _pollTimer.Start(); else _pollTimer.Stop();
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var parts = new List<string>();
        parts.Add(IsCapturing ? "App queries: ON" : "App queries: OFF");
        if (CaptureServerActivity && ServerActivityConnection is not null)
            parts.Add($"Server activity: {ServerActivityConnection.Name}");
        StatusText = string.Join("   •   ", parts);
    }

    private async System.Threading.Tasks.Task PollServerActivityAsync()
    {
        var conn = ServerActivityConnection;
        if (conn is null || !CaptureServerActivity) return;

        try
        {
            var activity = await Providers.ProviderRegistry.For(conn).GetServerActivityAsync(conn);
            foreach (var entry in activity.AsEnumerable().Reverse())
            {
                if (_seenServerKeys.Add(entry.DedupKey))
                    QueryProfiler.Instance.Log(entry);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Server activity error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Clear()
    {
        QueryProfiler.Instance.Clear();
        _seenServerKeys.Clear();
    }
}
