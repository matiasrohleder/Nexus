using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.Providers;
using Nexus.Providers.Backup;
using Nexus.Services;

namespace Nexus.ViewModels;

/// <summary>What the view's picker should open for a backup path.</summary>
public enum BackupPickKind { SaveFile, OpenFile, Folder }

/// <summary>
/// The Backup / Restore dialog (an in-window overlay, like the other dialogs). Opened from the
/// explorer for a database, a container or a whole connection — whatever the engine's
/// <see cref="IBackupStrategy.Scope"/> says. Shows whether the needed tool is installed (and
/// lets the user locate it), runs the operation with a live log, and can cancel it.
/// </summary>
public partial class BackupDialogViewModel : ViewModelBase
{
    private const int MaxLogLines = 2000;

    private IBackupStrategy? _strategy;
    private BackupTarget? _target;
    private Func<string, Task>? _onRestored;
    private CancellationTokenSource? _cts;
    private readonly Queue<string> _log = new();
    private bool _flushQueued;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackup), nameof(IsRestore), nameof(StartText), nameof(PathLabel))]
    private BackupDirection _direction;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))] private string _path = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FormatChoice))] private BackupFormat? _selectedFormat;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))] private string _targetDatabase = "";
    [ObservableProperty] private bool _replace;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(CanLocateTool), nameof(LocateToolText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private BackupReadiness _readiness = BackupReadiness.Ok("");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanLocateTool))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CloseCommand), nameof(BrowseCommand))]
    private bool _isRunning;
    [ObservableProperty] private string _logText = "";
    /// <summary>Outcome line under the log: null while idle/running, then success or failure.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOutcome))] private string? _outcome;
    [ObservableProperty] private bool _succeeded;

    public bool IsBackup => Direction == BackupDirection.Backup;
    public bool IsRestore => Direction == BackupDirection.Restore;
    public bool IsReady => Readiness.Ready;
    public bool CanEdit => !IsRunning;
    public bool CanLocateTool => Readiness.Tool is not null && !IsRunning;
    public string LocateToolText => Readiness.Ready ? "Change…" : "Locate…";
    public bool HasOutcome => Outcome is not null;
    public bool HasLog => LogText.Length > 0;

    public IReadOnlyList<BackupFormat> Formats => _strategy?.Formats ?? Array.Empty<BackupFormat>();

    /// <summary>ComboBox proxy: the two-way binding pushes null while ItemsSource is swapped; ignore it.</summary>
    public BackupFormat? FormatChoice
    {
        get => SelectedFormat;
        set { if (value is not null) SelectedFormat = value; }
    }
    public bool HasFormatChoice => IsBackup && Formats.Count > 1;
    public bool ShowTargetDatabase => IsRestore && _strategy?.Scope != BackupScope.Connection;
    public bool ShowReplace => IsRestore && IsReady;
    public string ReplaceHint => _strategy?.ReplaceHint ?? "";
    public bool IsServerPath => _strategy?.Location == BackupLocation.ServerFile;
    public bool CanBrowse => !IsServerPath;
    public string StartText => IsBackup ? "Back up" : "Restore";

    public string PathLabel => (_strategy?.Location, IsBackup) switch
    {
        (BackupLocation.ServerFile, _) => "Backup file on the server",
        (BackupLocation.LocalFolder, true) => "Export to folder (created by the backup)",
        (BackupLocation.LocalFolder, false) => "Export folder to restore",
        (_, true) => "Save backup as",
        _ => "Backup file to restore"
    };

    /// <summary>Wired by the view: file/folder picker (kind, suggested name, extension) → path or null.</summary>
    public Func<BackupPickKind, string, string, Task<string?>>? PickPath { get; set; }

    /// <summary>Wired by the view: lets the user pick a tool's executable → path or null.</summary>
    public Func<string, Task<string?>>? PickTool { get; set; }

    /// <summary>Wired by the main window: the overlay confirmation (restores are destructive).</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>
    /// Opens the dialog for <paramref name="target"/>. <paramref name="label"/> names it for the
    /// user; <paramref name="onRestored"/> runs after a successful restore with the target database.
    /// </summary>
    public void Open(BackupDirection direction, IBackupStrategy strategy, BackupTarget target, string label,
        string engine, Func<string, Task>? onRestored)
    {
        if (IsRunning) return;
        _strategy = strategy;
        _target = target;
        _onRestored = onRestored;
        Direction = direction;
        Title = direction == BackupDirection.Backup ? $"Back up {label}" : "Restore a backup";
        Subtitle = direction == BackupDirection.Backup || label == target.Connection.Name
            ? $"{engine} · {target.Connection.Name}"
            : $"{engine} · {target.Connection.Name} · {label}";
        OnPropertyChanged(nameof(Formats));
        SelectedFormat = strategy.Formats.FirstOrDefault();
        TargetDatabase = target.Database;
        Replace = false;
        Path = "";
        Outcome = null;
        ClearLog();
        RefreshReadiness();
        OnPropertyChanged(nameof(HasFormatChoice));
        OnPropertyChanged(nameof(ShowTargetDatabase));
        OnPropertyChanged(nameof(ReplaceHint));
        OnPropertyChanged(nameof(IsServerPath));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(PathLabel));
        IsOpen = true;

        if (direction == BackupDirection.Backup)
            _ = SuggestPathAsync();
    }

    private void RefreshReadiness()
    {
        Readiness = _strategy?.Check(Direction) ?? BackupReadiness.Unsupported("");
        OnPropertyChanged(nameof(ShowReplace));
    }

    private string SuggestedName =>
        $"{Sanitize(_target?.Database is { Length: > 0 } db ? db : _target?.Connection.Name ?? "backup")}-{DateTime.Now:yyyyMMdd-HHmm}" +
        (SelectedFormat?.Extension ?? "");

    private static string Sanitize(string name) =>
        string.Concat(name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));

    /// <summary>Pre-fills a backup path: the server's backup folder, or the user's Documents folder.</summary>
    private async Task SuggestPathAsync()
    {
        if (_strategy is null || _target is null) return;
        if (_strategy.Location == BackupLocation.ServerFile)
        {
            try
            {
                var suggested = await _strategy.SuggestServerPathAsync(_target, SuggestedName, CancellationToken.None);
                if (suggested is not null && Path.Length == 0) Path = suggested;
            }
            catch (Exception ex) { Readiness = BackupReadiness.Unsupported($"Could not reach the server: {ex.Message}"); }
            return;
        }

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!Directory.Exists(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Path = System.IO.Path.Combine(docs, SuggestedName);
    }

    partial void OnSelectedFormatChanged(BackupFormat? oldValue, BackupFormat? newValue)
    {
        // Keep the suggested file's extension in step with the chosen format.
        if (oldValue is null || newValue is null || !IsBackup || oldValue.Extension.Length == 0) return;
        if (Path.EndsWith(oldValue.Extension, StringComparison.OrdinalIgnoreCase))
            Path = Path[..^oldValue.Extension.Length] + newValue.Extension;
    }

    partial void OnLogTextChanged(string value) => OnPropertyChanged(nameof(HasLog));

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task BrowseAsync()
    {
        if (PickPath is null || _strategy is null) return;
        var ext = SelectedFormat?.Extension ?? "";
        string? picked;
        if (_strategy.Location == BackupLocation.LocalFolder)
        {
            picked = await PickPath(BackupPickKind.Folder, "", "");
            // A backup exports into a new folder inside the one picked.
            if (picked is not null && IsBackup) picked = System.IO.Path.Combine(picked, SuggestedName);
        }
        else
            picked = await PickPath(IsBackup ? BackupPickKind.SaveFile : BackupPickKind.OpenFile, SuggestedName, ext);
        if (picked is not null) Path = picked;
    }

    [RelayCommand]
    private async Task LocateToolAsync()
    {
        if (PickTool is null || Readiness.Tool is not { } tool) return;
        var picked = await PickTool(tool);
        if (picked is null) return;
        ExternalTools.SetCustomPath(tool, picked);
        RefreshReadiness();
    }

    private bool CanStart() =>
        !IsRunning && IsReady && Path.Trim().Length > 0 && (!ShowTargetDatabase || TargetDatabase.Trim().Length > 0);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_strategy is null || _target is null) return;
        var path = Path.Trim();
        var target = ShowTargetDatabase ? _target with { Database = TargetDatabase.Trim() } : _target;
        var name = target.Database.Length > 0 ? target.Database : _target.Connection.Name;

        if (IsRestore && Confirm is not null && !await Confirm(
                $"Restore {System.IO.Path.GetFileName(path.TrimEnd('/', '\\'))} into \"{name}\"?\n\n" +
                (Replace ? $"Existing data will be overwritten: {ReplaceHint}" : "Objects in the backup are added to what is there.")))
            return;

        ClearLog();
        Outcome = null;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var log = new Progress<string>(AppendLog);
        try
        {
            // Off the UI thread: strategies do synchronous file I/O and driver work.
            if (IsBackup)
                await Task.Run(() => BackupRunner.BackupAsync(_strategy, target, path, SelectedFormat ?? _strategy.Formats[0], log, _cts.Token));
            else
                await Task.Run(() => BackupRunner.RestoreAsync(_strategy, target, path, Replace, log, _cts.Token));

            Succeeded = true;
            Outcome = IsBackup ? "Backup completed." : $"Restore into {name} completed.";
            if (IsRestore && _onRestored is not null) await _onRestored(target.Database);
        }
        catch (OperationCanceledException)
        {
            Succeeded = false;
            Outcome = "Cancelled.";
        }
        catch (Exception ex)
        {
            Succeeded = false;
            Outcome = ex is BackupException ? ex.Message : $"{(IsBackup ? "Backup" : "Restore")} failed: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
            FlushLog();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Close() => IsOpen = false;

    // Tools can print thousands of lines: keep the tail and repaint at most once per dispatcher pass.
    private void AppendLog(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines) _log.Dequeue();
        if (_flushQueued) return;
        _flushQueued = true;
        Dispatcher.UIThread.Post(FlushLog, DispatcherPriority.Background);
    }

    private void FlushLog()
    {
        _flushQueued = false;
        LogText = string.Join('\n', _log);
    }

    private void ClearLog()
    {
        _log.Clear();
        LogText = "";
    }
}
