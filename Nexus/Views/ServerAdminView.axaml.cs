using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexus.ViewModels;

namespace Nexus.Views;

public partial class ServerAdminView : UserControl
{
    public ServerAdminView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ServerAdminViewModel vm)
                vm.PickBackupPath = PickBackupPathAsync;
        };
    }

    private async Task<string?> PickBackupPathAsync(string suggestedName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Backup destination",
            SuggestedFileName = suggestedName
        });
        return file?.TryGetLocalPath();
    }
}
