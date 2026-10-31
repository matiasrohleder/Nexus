using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexus.Models;

namespace Nexus.ViewModels;

public enum NodeKind { Folder, Connection, Database, Schema, Table, View, Column, Message }

/// <summary>Where a dragged explorer node would land relative to the node under the pointer.</summary>
public enum DropPosition { None, Before, After, Into }

/// <summary>
/// A lazily-loaded node in the object-explorer tree. Children are fetched on first expand via
/// an injected <see cref="_loader"/>, so opening a connection doesn't block on loading every schema.
/// </summary>
public partial class TreeNodeViewModel : ObservableObject
{
    private static readonly TreeNodeViewModel Placeholder =
        new("…", "", NodeKind.Message, null);

    private readonly Func<TreeNodeViewModel, Task>? _loader;
    private bool _loaded;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoading;

    /// <summary>Drag-and-drop feedback: line above/below the row, or highlighted row (drop into folder).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDropBefore), nameof(IsDropAfter), nameof(IsDropInto))]
    private DropPosition _dropHint;

    public bool IsDropBefore => DropHint == DropPosition.Before;
    public bool IsDropAfter => DropHint == DropPosition.After;
    public bool IsDropInto => DropHint == DropPosition.Into;

    /// <summary>Connection roots: holds live server connections (see Services.ConnectionStatus).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusFill), nameof(StatusStroke), nameof(StatusText),
        nameof(CanConnect), nameof(CanDisconnect))]
    private bool _isConnected;

    /// <summary>Connection roots: the last attempt to open the connection failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusFill), nameof(StatusStroke), nameof(StatusText))]
    private bool _hasConnectError;

    // Status dot next to the engine logo: hollow gray = disconnected, green = connected, red = failed.
    public string StatusFill => HasConnectError ? "#F87171" : IsConnected ? "#4ADE80" : "Transparent";
    public string StatusStroke => HasConnectError ? "#F87171" : IsConnected ? "#4ADE80" : "#6B7280";
    public string? StatusText => !IsConnection ? null
        : HasConnectError ? "Connection failed — right-click › Connect to retry"
        : IsConnected ? "Connected"
        : "Disconnected — expand to connect";

    public string Name { get; }
    /// <summary>Line-icon key for <see cref="Controls.Icon"/> ("table", "folder"…); empty = no icon.</summary>
    public string Icon { get; }
    public NodeKind Kind { get; }

    /// <summary>Icon tint; defaults per kind so the tree reads at a glance.</summary>
    public string IconColor { get; init; }

    /// <summary>Muted text after the name (column type, nullability).</summary>
    public string? Detail { get; init; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>Connection roots: engine id (picks the logo) + brand color, and the user's accent color.</summary>
    public string? Engine { get; init; }
    public string BrandColor { get; init; } = "#3F3F46";
    public string AccentColor { get; init; } = "Transparent";
    public bool HasLogo => Engine is not null;
    public bool HasIcon => Engine is null && Icon.Length > 0;
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    /// <summary>Connection this node belongs to (set for every node under a connection root).</summary>
    public SavedConnection? Connection { get; set; }
    public string? Schema { get; set; }
    public string? Table { get; set; }

    /// <summary>Owning node in the tree (null for connection roots). Set when children are added.</summary>
    public TreeNodeViewModel? Parent { get; set; }

    public bool IsConnection => Kind == NodeKind.Connection;
    public bool IsFolder => Kind == NodeKind.Folder;
    /// <summary>Connections and folders can be reordered / regrouped (menu or drag and drop).</summary>
    public bool CanMove => Kind is NodeKind.Connection or NodeKind.Folder;
    public bool CanRefresh => Kind != NodeKind.Folder;
    public bool IsQueryable => Kind is NodeKind.Table or NodeKind.View;

    // Context-menu visibility per node kind (the explorer menu binds to these on the selected node).
    public bool IsTable => Kind == NodeKind.Table;
    public bool CanOpenQuery => Kind is NodeKind.Connection or NodeKind.Database or NodeKind.Schema;
    public bool CanAdminServer => Kind is NodeKind.Connection or NodeKind.Database;
    public bool CanDrop => Kind is NodeKind.Database or NodeKind.Schema or NodeKind.Table or NodeKind.View;
    public bool HasContextMenu => Kind is not (NodeKind.Column or NodeKind.Message);
    public bool CanConnect => IsConnection && !IsConnected;
    public bool CanDisconnect => IsConnection && IsConnected;

    /// <summary>Adds a child and records this node as its parent.</summary>
    public void AddChild(TreeNodeViewModel child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public TreeNodeViewModel(string name, string icon, NodeKind kind, Func<TreeNodeViewModel, Task>? loader)
    {
        Name = name;
        Icon = icon;
        Kind = kind;
        IconColor = kind switch
        {
            NodeKind.Folder => "#A1A1AA",
            NodeKind.Database => "#60A5FA",
            NodeKind.Schema => "#FBBF24",
            NodeKind.Table => "#7DD3FC",
            NodeKind.View => "#C4B5FD",
            NodeKind.Message when icon == "warning" => "#F87171",
            _ => "#9CA3AF"
        };
        _loader = loader;
        if (_loader is not null)
            Children.Add(Placeholder); // gives the node an expander arrow before its real children load
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _ = EnsureLoadedAsync();
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded || _loader is null) return;
        _loaded = true;
        IsLoading = true;
        Children.Clear();
        try
        {
            await _loader(this);
        }
        catch (Exception ex)
        {
            Children.Add(new TreeNodeViewModel($"Error: {ex.Message}", "warning", NodeKind.Message, null));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Forces the node to reload its children on next expand (used by "Refresh").</summary>
    public void Invalidate()
    {
        _loaded = false;
        Children.Clear();
        if (_loader is not null)
            Children.Add(Placeholder);
        IsExpanded = false;
    }

    /// <summary>Reloads children in place, keeping the node expanded (used after Drop/DDL changes).</summary>
    public async Task ReloadAsync()
    {
        if (_loader is null) return;
        _loaded = true;
        IsLoading = true;
        Children.Clear();
        try
        {
            await _loader(this);
        }
        catch (Exception ex)
        {
            Children.Add(new TreeNodeViewModel($"Error: {ex.Message}", "warning", NodeKind.Message, null));
        }
        finally
        {
            IsLoading = false;
        }
    }
}
