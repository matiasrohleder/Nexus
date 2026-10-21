using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexus.ViewModels;

/// <summary>Base for anything hosted as a main-area tab (query editor, data editor, server admin).</summary>
public abstract partial class DocumentViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";

    /// <summary>Line-icon key shown in the tab header (see <see cref="Controls.Icon"/>).</summary>
    public abstract string Icon { get; }
}
