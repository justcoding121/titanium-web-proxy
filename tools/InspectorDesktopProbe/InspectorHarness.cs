using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Titanium.Inspector;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Inspector.Views;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.DesktopProbe;

/// <summary>
/// In-process desktop Avalonia Inspector with real OS proxy controller and auto-accept dialogs.
/// OS CryptUI / Keychain password still require a human click once.
/// </summary>
public sealed class InspectorHarness : IAsyncDisposable
{
    private readonly string _settingsPath;
    private bool _disposed;

    public MainWindowViewModel ViewModel { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    public ProbeUiRobot Robot { get; private set; } = null!;
    public ScriptedInspectorDialogs Dialogs { get; } = new();
    public InterceptionService Interception { get; private set; } = null!;
    public ProbeLog Log { get; }

    private InspectorHarness(ProbeLog log)
    {
        Log = log;
        _settingsPath = Path.Combine(Path.GetTempPath(), "twp-desktop-probe-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public static async Task<InspectorHarness> StartAsync(ProbeLog log)
    {
        CertificateManager.SuppressInteractiveRootStoreMutations = false;
        Environment.SetEnvironmentVariable("TITANIUM_INSPECTOR_SKIP_AUTO_MAINWINDOW", "1");
        Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", "");

        var harness = new InspectorHarness(log);
        await harness.CreateUiAsync().ConfigureAwait(true);
        return harness;
    }

    private async Task CreateUiAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var settings = new SettingsService(_settingsPath);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Current.CheckForUpdatesOnStartup = false;
            settings.Current.DecryptHttps = false;
            settings.Save();

            var registry = new SessionRegistry();
            var buffer = new SessionStreamBuffer(registry);
            var updates = new UpdateService(settings);
            Interception = new InterceptionService(new ProxyServerSystemProxyController())
            {
                DecryptHttps = false,
                IgnoreServerCertificateErrors = true,
                ProxyLoopback = true,
            };

            (ViewModel, Window) = InspectorAppFactory.CreateMainWindow(
                settings, buffer, registry, updates, Interception, Dialogs);

            ViewModel.BindPort = 0;
            ViewModel.BindAddress = "127.0.0.1";
            ViewModel.AutoStartCapture = false;
            ViewModel.AutoSystemProxyOnStart = false;

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = Window;

            Window.Width = 1280;
            Window.Height = 800;
            Window.Show();
            Robot = new ProbeUiRobot(Window);
            Dispatcher.UIThread.RunJobs();
        });
    }

    public async Task OnUiAsync(Action action)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            action();
            Dispatcher.UIThread.RunJobs();
        });
    }

    public async Task OnUiAsync(Func<Task> action)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await action().ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        });
    }

    public async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var met = false;
            await OnUiAsync(() => met = condition()).ConfigureAwait(true);
            if (met)
                return;
            await Task.Delay(50).ConfigureAwait(true);
        }

        throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:0}s");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            await OnUiAsync(() =>
            {
                try { ViewModel.SystemProxy = false; } catch { /* ignore */ }
                try { ViewModel.EnsureShutdown(); } catch { /* ignore */ }
                try { Window.Close(); } catch { /* ignore */ }
            }).ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }

        try { Interception.SetSystemProxy(false); } catch { /* ignore */ }
        try { Interception.Dispose(); } catch { /* ignore */ }

        try
        {
            if (File.Exists(_settingsPath))
                File.Delete(_settingsPath);
        }
        catch
        {
            // ignore
        }
    }
}
