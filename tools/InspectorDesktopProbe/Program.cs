using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Titanium.Inspector;
using Titanium.Inspector.DesktopProbe.Scenarios;
using Titanium.Inspector.DesktopProbe.Shared;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.DesktopProbe;

internal static class Program
{
    private static int _exitCode = 1;
    private static string[] _args = Array.Empty<string>();

    [STAThread]
    public static int Main(string[] args)
    {
        _args = args;
        var cmd = args.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "help";

        // status can run without Avalonia
        if (cmd is "help" or "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        CertificateManager.SuppressInteractiveRootStoreMutations = false;
        // Do not set TITANIUM_SKIP_ROOT_STORE_UI — interactive trust is expected.

        if (cmd == "status" && !args.Any(a => a is "--ui"))
        {
            using var log = new ProbeLog(ResolveResultsDir());
            var code = StatusScenario.Run(log, harness: null);
            log.WriteSummary(cmd, code);
            return code;
        }

        Environment.SetEnvironmentVariable("TITANIUM_INSPECTOR_SKIP_AUTO_MAINWINDOW", "1");

        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            builder = builder.With(new AvaloniaNativePlatformOptions
            {
                OverlayPopups = true,
            });
        }

        // Schedule probe work once the desktop lifetime is up.
        builder.AfterSetup(static __ =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Startup += (_, _) => { _ = RunProbeAsync(); };
            }
        });

        builder.StartWithClassicDesktopLifetime(Array.Empty<string>());
        return _exitCode;
    }

    private static async Task RunProbeAsync()
    {
        var cmd = _args.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "help";
        var browser = GetOption(_args, "--browser") ?? "auto";
        var timeoutSec = int.TryParse(GetOption(_args, "--timeout-sec"), out var t) ? t : 45;
        var timeout = TimeSpan.FromSeconds(timeoutSec);

        using var log = new ProbeLog(ResolveResultsDir());
        InspectorHarness? harness = null;
        try
        {
            harness = await InspectorHarness.StartAsync(log).ConfigureAwait(true);
            log.Info($"Harness ready. command={cmd} browser={browser} timeout={timeoutSec}s");

            _exitCode = cmd switch
            {
                "status" => StatusScenario.Run(log, harness),
                "proxy" => await ProxyScenario.RunAsync(harness, log, browser, timeout).ConfigureAwait(true),
                "cert" => await CertScenario.RunAsync(harness, log, browser, timeout).ConfigureAwait(true),
                "firefox" => await FirefoxScenario.RunAsync(harness, log, timeout).ConfigureAwait(true),
                "loopback" => await LoopbackScenario.RunAsync(harness, log).ConfigureAwait(true),
                "exclusions" => await ExclusionsScenario.RunAsync(harness, log).ConfigureAwait(true),
                "pac" => await PacScenario.RunAsync(harness, log).ConfigureAwait(true),
                "all" => await RunAllAsync(harness, log, browser, timeout).ConfigureAwait(true),
                _ => FailUnknown(log, cmd),
            };
        }
        catch (Exception ex)
        {
            log.Error(ex.ToString());
            _exitCode = 1;
        }
        finally
        {
            if (harness is not null)
                await harness.DisposeAsync().ConfigureAwait(true);
            log.WriteSummary(cmd, _exitCode);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(_exitCode);
            else
                Environment.Exit(_exitCode);
        }
    }

    private static async Task<int> RunAllAsync(InspectorHarness harness, ProbeLog log, string browser, TimeSpan timeout)
    {
        var codes = new List<int>
        {
            StatusScenario.Run(log, harness),
            await ProxyScenario.RunAsync(harness, log, browser, timeout).ConfigureAwait(true),
            await CertScenario.RunAsync(harness, log, browser, timeout).ConfigureAwait(true),
            await ExclusionsScenario.RunAsync(harness, log).ConfigureAwait(true),
            await PacScenario.RunAsync(harness, log).ConfigureAwait(true),
        };

        if (OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            codes.Add(await LoopbackScenario.RunAsync(harness, log).ConfigureAwait(true));

        if (BrowserPaths.FindFirefox() is not null)
            codes.Add(await FirefoxScenario.RunAsync(harness, log, timeout).ConfigureAwait(true));

        return codes.Any(c => c != 0) ? 1 : 0;
    }

    private static int FailUnknown(ProbeLog log, string cmd)
    {
        log.Error($"Unknown command: {cmd}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            InspectorDesktopProbe — on-demand Inspector UX + OS proxy/CA validation (not CI).

            Usage:
              dotnet run --project tools/InspectorDesktopProbe -- <command> [--browser auto|edge|chrome|firefox] [--timeout-sec 45]

            Commands:
              status       Dump OS proxy / trust / last-run.json (add --ui for live harness)
              proxy        Start capture, toggle System proxy, capture via OS proxy (no --proxy-server)
              cert         Install/Remove CA via menus; assert Decrypt HTTPS auto-off after remove
              firefox      Trust CA in Firefox + system-proxy HTTPS capture
              loopback     Windows: Allow Store apps dialog
              exclusions   Excluded hosts + Proxy localhost
              pac          PAC replace confirm cancel/accept (when PAC active)
              all          Run applicable scenarios for this OS

            Logs: tools/InspectorDesktopProbe/results/ (last-run.json for MCP)

            Note: Windows Trusted Root Yes/No and macOS Keychain password still need a human click once.
            """);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static string ResolveResultsDir()
    {
        // Prefer source-tree results/ when running via dotnet run
        var probeDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var candidate = Path.Combine(probeDir, "results");
        if (Directory.Exists(probeDir) &&
            File.Exists(Path.Combine(probeDir, "InspectorDesktopProbe.csproj")))
            return candidate;

        return Path.Combine(Path.GetTempPath(), "ti-inspector-desktop-probe");
    }
}
