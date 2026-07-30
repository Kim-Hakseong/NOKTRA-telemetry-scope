using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ts.App.ViewModels;
using Ts.App.Views;

namespace Ts.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            var options = StartupOptions.Parse(desktop.Args ?? Array.Empty<string>());
            if (window.DataContext is MainWindowViewModel model)
            {
                // After the window is up, so a failure lands in the status bar of a visible window
                // rather than taking the application down before anything is drawn.
                Dispatcher.UIThread.Post(
                    () => _ = model.ApplyStartupAsync(options), DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
