using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Nexus.ViewModels;

namespace Nexus.Views;

public partial class ConnectionEditorWindow : Window
{
    public ConnectionEditorWindow()
    {
        InitializeComponent();
        SaveButton.Click += OnSave;
        CancelButton.Click += OnCancel;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConnectionEditorViewModel vm)
                vm.PickFile = PickDatabaseFileAsync;
        };
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ConnectionEditorViewModel vm && !vm.IsBusy)
        {
            vm.TestCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
    }

    private async Task<string?> PickDatabaseFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose database file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Database files") { Patterns = new[] { "*.db", "*.sqlite", "*.sqlite3", "*.duckdb", "*.ddb" } },
                FilePickerFileTypes.All
            }
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
