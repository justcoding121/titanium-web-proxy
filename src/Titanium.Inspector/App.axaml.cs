using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Inspector.Views;
using Titanium.Web.Proxy.Abstractions.Updates;

namespace Titanium.Inspector;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = SettingsService.Load();
        LanguageService.Apply(settings.Current.UiLanguage);
        ThemeService.ApplyThemeMode(settings.Current.ThemeMode);
        var sessions = new SessionRegistry(SessionStoreOptions.FromSettings(settings.Current));
        var buffer = new SessionStreamBuffer();
        // Holder filled after MainWindowViewModel constructs InterceptionService.
        InterceptionService? interception = null;
        var updates = new UpdateService(
            settings,
            httpFactory: () => UpdateHttp.CreateClient(
                TimeSpan.FromMinutes(5),
                proxyUri => UpdateHttp.IsOwnLoopbackProxy(
                    proxyUri,
                    interception?.BoundPort ?? 0,
                    interception is { IsRunning: true, SystemProxyEnabled: true })),
            updateTrafficMayBeCaptured: () =>
                interception is { IsRunning: true, SystemProxyEnabled: true, DecryptHttps: true });
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
            interception = vm.Interception;
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };

            // Wait for the background close path (or run shutdown if Closing never fired).
            // ForceClear covers Mac AppleScript quit where RestoreOriginalSettings can race
            // ProcessExit and leave loopback networksetup enabled. Linux must not run it
            // again: the graceful clear just started a fail-open helper for a pinned browser.
            desktop.Exit += (_, _) =>
            {
                vm.EnsureShutdown();
                if (OperatingSystem.IsMacOS())
                {
                    try
                    {
                        Titanium.Web.Proxy.Helpers.InstalledProxyCleanup.ForceClear();
                    }
                    catch
                    {
                        // best-effort
                    }
                }
            };
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
