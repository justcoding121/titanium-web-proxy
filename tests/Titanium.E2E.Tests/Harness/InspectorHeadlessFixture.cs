using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Titanium.Inspector;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Inspector.Views;

namespace Titanium.E2E.Tests.Harness;

/// <summary>Creates a headless MainWindow with test seams (no auto-start / OS proxy / machine CA).</summary>
public sealed class InspectorHeadlessFixture : IAsyncDisposable
{
    private readonly string _settingsPath;
    private HeadlessUnitTestSession? _session;

    public InspectorHeadlessFixture()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), "twp-ui-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public MainWindowViewModel ViewModel { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    public InspectorUiRobot Robot { get; private set; } = null!;
    public ScriptedInspectorDialogs Dialogs { get; } = new();
    public ScriptedInspectorPathPicker PathPicker { get; } = new();
    public RecordingSystemProxyController Proxy { get; } = new();
    public InterceptionService Interception { get; private set; } = null!;

    public async Task StartAsync(bool visualSkia = false)
    {
        // visualSkia retained for call-site clarity; session always uses Skia.
        _ = visualSkia;
        _session = InspectorHeadlessSessionHost.Get();

        await _session.Dispatch(() =>
        {
            var settings = new SettingsService(_settingsPath);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();

            var registry = new SessionRegistry();
            var buffer = new SessionStreamBuffer(registry);
            var updates = new UpdateService(settings);
            Interception = new InterceptionService(Proxy) { UseInMemoryTrustState = true };
            (ViewModel, Window) = InspectorAppFactory.CreateMainWindow(
                settings, buffer, registry, updates, Interception, Dialogs, PathPicker);
            ViewModel.BindPort = CliProcessHarness.GetFreePort();
            ViewModel.BindAddress = "127.0.0.1";
            ViewModel.AutoStartCapture = false;
            ViewModel.AutoSystemProxyOnStart = false;

            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = Window;
            }

            Window.Width = 1280;
            Window.Height = 800;
            Window.Show();
            Robot = new InspectorUiRobot(Window);
            Dispatcher.UIThread.RunJobs();
        }, CancellationToken.None);
    }

    public Task DispatchAsync(Action action) =>
        _session!.Dispatch(() =>
        {
            action();
            Dispatcher.UIThread.RunJobs();
        }, CancellationToken.None);

    public async Task DispatchAsync(Func<Task> action) =>
        await _session!.Dispatch(async () =>
        {
            await action();
            Dispatcher.UIThread.RunJobs();
        }, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            try
            {
                await _session.Dispatch(() =>
                {
                    try { ViewModel.EnsureShutdown(); } catch { /* ignore */ }
                    try { Window.Close(); } catch { /* ignore */ }
                    // Drain pending layout/render before Headless resets the locator (avoids IFontManagerImpl races).
                    Dispatcher.UIThread.RunJobs();
                    Dispatcher.UIThread.RunJobs();
                }, CancellationToken.None);
            }
            catch { /* ignore teardown */ }

            // Keep shared session alive for subsequent tests.
            _session = null;
        }

        try { File.Delete(_settingsPath); } catch { /* ignore */ }
    }
}
