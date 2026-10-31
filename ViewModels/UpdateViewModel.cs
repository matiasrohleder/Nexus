using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Services;

namespace Nexus.ViewModels;

public enum UpdateStage { Checking, UpToDate, Available, Downloading, Installing, Failed }

/// <summary>
/// The update dialog (an in-window overlay like the other dialogs) and the top-bar badge:
/// startup check, manual "Check for updates…", download progress, install, relaunch.
/// </summary>
public partial class UpdateViewModel : ViewModelBase
{
    private readonly AppSettings _settings = AppSettings.Current;
    private CancellationTokenSource? _cts;
    private ReleaseInfo? _release;
    private string? _package;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsUpToDate), nameof(IsAvailable), nameof(IsBusy),
        nameof(IsDownloading), nameof(IsFailed), nameof(CanDismiss))]
    private UpdateStage _stage;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNotes))] private string _notes = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BadgeText))] private string? _pendingVersion;
    [ObservableProperty] private bool _canInstallInApp;
    [ObservableProperty] private bool _canOpenPackage;

    public bool IsChecking => Stage == UpdateStage.Checking;
    public bool IsUpToDate => Stage == UpdateStage.UpToDate;
    public bool IsAvailable => Stage == UpdateStage.Available;
    public bool IsDownloading => Stage == UpdateStage.Downloading;
    public bool IsBusy => Stage is UpdateStage.Downloading or UpdateStage.Installing or UpdateStage.Checking;
    public bool IsFailed => Stage == UpdateStage.Failed;
    /// <summary>Installing can't be interrupted (package managers are mid-flight).</summary>
    public bool CanDismiss => Stage != UpdateStage.Installing;
    public bool HasNotes => Notes.Length > 0;
    public string BadgeText => $"Update to {PendingVersion}";

    public string InstallButtonText => CanInstallInApp ? "Update now" : "Download";

    public bool CheckOnStartup
    {
        get => _settings.CheckForUpdatesOnStartup;
        set
        {
            if (_settings.CheckForUpdatesOnStartup == value) return;
            _settings.CheckForUpdatesOnStartup = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Wired by the View: closes the app so the relaunch helper / installer can take over.</summary>
    public Action? Quit { get; set; }

    partial void OnCanInstallInAppChanged(bool value) => OnPropertyChanged(nameof(InstallButtonText));

    /// <summary>Quiet check when the window opens: only speaks up for a new, non-skipped release.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!_settings.CheckForUpdatesOnStartup) return;
        try
        {
            var release = await Task.Run(() => Updater.GetLatestAsync());
            if (!Updater.IsNewer(release.Version, AppInfo.Version)) return;
            ShowAvailable(release);
            if (release.Version != _settings.SkippedUpdateVersion)
                IsOpen = true;
        }
        catch { /* offline, rate-limited… the startup check never bothers the user */ }
    }

    [RelayCommand]
    private async Task CheckNow()
    {
        if (IsBusy) { IsOpen = true; return; }
        Stage = UpdateStage.Checking;
        CanOpenPackage = false;
        Title = "Checking for updates…";
        Message = "";
        Notes = "";
        IsOpen = true;
        try
        {
            var release = await Task.Run(() => Updater.GetLatestAsync());
            if (Updater.IsNewer(release.Version, AppInfo.Version))
            {
                ShowAvailable(release);
                return;
            }
            PendingVersion = null;
            Stage = UpdateStage.UpToDate;
            Title = "Nexus is up to date";
            Message = $"You have the latest version, {AppInfo.Version}.";
        }
        catch (Exception ex)
        {
            Fail("Couldn't check for updates", $"{ex.Message}\n\nYou can look for new versions on the releases page.", canOpenPackage: false);
        }
    }

    private void ShowAvailable(ReleaseInfo release)
    {
        _release = release;
        PendingVersion = release.Version;
        CanInstallInApp = Updater.AssetFor(release) is not null;
        CanOpenPackage = false;
        Stage = UpdateStage.Available;
        Title = $"Nexus {release.Version} is available";
        Message = CanInstallInApp
            ? $"You have {AppInfo.Version}. Nexus downloads the update, installs it and restarts — open query tabs are closed, so save anything you need first."
            : Updater.Kind == InstallKind.Manual
                ? $"You have {AppInfo.Version}. This copy wasn't installed by a Nexus installer, so it can't update itself — download the new version from the releases page."
                : $"You have {AppInfo.Version}. This release has no package for your system yet — download it from the releases page.";
        Notes = release.Notes;
    }

    /// <summary>Top-bar badge: reopens the dialog for the pending release.</summary>
    [RelayCommand]
    private void Show()
    {
        if (_release is not null && Stage is UpdateStage.UpToDate or UpdateStage.Failed)
            ShowAvailable(_release);
        IsOpen = true;
    }

    [RelayCommand]
    private async Task Install()
    {
        if (_release is null) return;
        var asset = Updater.AssetFor(_release);
        if (asset is null)
        {
            OpenPage();
            return;
        }

        _cts = new CancellationTokenSource();
        try
        {
            Stage = UpdateStage.Downloading;
            Title = $"Downloading Nexus {_release.Version}…";
            Message = asset.Name;
            Progress = 0;
            ProgressText = "";
            var progress = new Progress<double>(f =>
            {
                Progress = f * 100;
                ProgressText = asset.Size > 0
                    ? $"{f * asset.Size / 1048576:0.0} of {asset.Size / 1048576.0:0.0} MB"
                    : $"{f:P0}";
            });
            var release = _release;
            _package = await Task.Run(() => Updater.DownloadAsync(release, asset, progress, _cts.Token));

            Stage = UpdateStage.Installing;
            Title = $"Installing Nexus {_release.Version}…";
            Message = Updater.Kind is InstallKind.LinuxDeb or InstallKind.LinuxRpm
                ? "Enter your password when the system asks for it."
                : Updater.Kind == InstallKind.WindowsSetup
                    ? "The installer takes over and reopens Nexus when it's done."
                    : "Nexus restarts when it's done.";
            ProgressText = "";
            var package = _package;
            await Task.Run(() => Updater.InstallAsync(Updater.Kind, package));

            Updater.RelaunchAfterExit(Updater.Kind);
            Quit?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ShowAvailable(_release);
        }
        catch (Exception ex)
        {
            Fail("The update couldn't be installed", ex.Message, canOpenPackage: _package is not null);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Fail(string title, string message, bool canOpenPackage)
    {
        Stage = UpdateStage.Failed;
        Title = title;
        Message = message;
        Notes = "";
        CanOpenPackage = canOpenPackage;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Later()
    {
        if (CanDismiss && !IsDownloading) IsOpen = false;
    }

    [RelayCommand]
    private void Skip()
    {
        if (_release is null) return;
        _settings.SkippedUpdateVersion = _release.Version;
        _settings.Save();
        PendingVersion = null;
        IsOpen = false;
    }

    [RelayCommand]
    private void OpenPage()
    {
        try { Updater.Open(_release?.PageUrl ?? Updater.ReleasesPage); }
        catch { /* no browser registered */ }
    }

    /// <summary>Failure fallback: hand the downloaded package to the OS (installer, .dmg, software center).</summary>
    [RelayCommand]
    private void OpenPackage()
    {
        if (_package is null) return;
        try { Updater.Open(_package); }
        catch { OpenPage(); }
    }
}
