using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Ts.App.Controls;
using Ts.App.ViewModels;

namespace Ts.App.Views;

public sealed partial class MainWindow : Window, IFileDialogs
{
    public MainWindow()
    {
        InitializeComponent();

        var model = new MainWindowViewModel(this);
        DataContext = model;
        this.FindControl<StripChart>("Chart")!.Model = model;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public async Task<string?> OpenAsync(string title, FileFilter filter)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { ToFileType(filter) },
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> SaveAsync(string title, string suggestedName, FileFilter filter)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = new[] { ToFileType(filter) },
        });

        return file?.TryGetLocalPath();
    }

    private static FilePickerFileType ToFileType(FileFilter filter)
        => new(filter.Name) { Patterns = filter.Patterns };
}
