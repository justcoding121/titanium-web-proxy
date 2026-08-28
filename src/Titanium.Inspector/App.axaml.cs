using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Inspector.Views;

namespace Titanium.Inspector;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = SettingsService.Load();
        var sessions = new SessionRegistry();
        var buffer = new SessionStreamBuffer(sessions);
        var updates = new UpdateService(settings);
        PlusInspectorLoader.TryLoadPanels(out _);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(buffer, sessions, updates, settings);
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };

            // Safety net if the main window Closing handler did not run (matches WPF App.OnExit).
            desktop.Exit += (_, _) => vm.EnsureShutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
