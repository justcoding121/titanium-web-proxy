using Avalonia;
using Avalonia.Controls;
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
        ThemeService.ApplyThemeMode(settings.Current.ThemeMode);
        var sessions = new SessionRegistry(SessionStoreOptions.FromSettings(settings.Current));
        var buffer = new SessionStreamBuffer();
        var updates = new UpdateService(settings);
        PlusInspectorLoader.TryLoadPanels(out _);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Headless / E2E fixtures build MainWindow via InspectorAppFactory.
            if (string.Equals(
                    Environment.GetEnvironmentVariable("TITANIUM_INSPECTOR_SKIP_AUTO_MAINWINDOW"),
                    "1",
                    StringComparison.Ordinal))
            {
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var vm = new MainWindowViewModel(buffer, sessions, updates, settings);
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };

            // Wait for the background close path (or run shutdown if Closing never fired).
            desktop.Exit += (_, _) => vm.EnsureShutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// macOS application menu About — same <see cref="MainWindowViewModel.OpenAboutCommand"/> as Help.
    /// </summary>
    private void OnMacAboutClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: Window window })
        {
            return;
        }

        if (window.DataContext is MainWindowViewModel vm
            && vm.OpenAboutCommand.CanExecute(null))
        {
            vm.OpenAboutCommand.Execute(null);
        }
    }
}
