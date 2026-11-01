using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexus.ViewModels;

namespace Nexus.Views;

public partial class BackupDialogView : UserControl
{
    public BackupDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is BackupDialogViewModel vm)
            {
                vm.PickPath = PickPathAsync;
                vm.PickTool = PickToolAsync;
            }
        };
    }

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async Task<string?> PickPathAsync(BackupPickKind kind, string suggestedName, string extension)
    {
        if (Storage is not { } storage) return null;
        var types = extension.Length > 0
            ? new[] { new FilePickerFileType($"Backup (*{extension})") { Patterns = new[] { "*" + extension } }, FilePickerFileTypes.All }
            : new[] { FilePickerFileTypes.All };

        switch (kind)
        {
            case BackupPickKind.SaveFile:
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save backup as",
                    SuggestedFileName = suggestedName,
                    DefaultExtension = extension.TrimStart('.'),
                    FileTypeChoices = types
                });
                return file?.TryGetLocalPath();

            case BackupPickKind.OpenFile:
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Backup to restore",
                    AllowMultiple = false,
                    FileTypeFilter = types
                });
                return files.Count > 0 ? files[0].TryGetLocalPath() : null;

            default:
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Choose a folder",
                    AllowMultiple = false
                });
                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
    }

    private async Task<string?> PickToolAsync(string tool)
    {
        if (Storage is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Locate {tool}",
            AllowMultiple = false
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
