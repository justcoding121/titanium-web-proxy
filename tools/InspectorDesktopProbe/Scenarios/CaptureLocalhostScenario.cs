using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Titanium.Inspector.DesktopProbe.Shared;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>
/// Clicks Capture local traffic with System proxy on and checks whether 127.0.0.1 still goes through Inspector.
/// </summary>
public static class CaptureLocalhostScenario
{
    private const string ProbePath = "/twp-loopback-probe";

    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        if (!OperatingSystem.IsWindows())
        {
            log.Step("capture-localhost", true, "Skipped (Windows WinINET only)");
            return 0;
        }

        await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        log.Step("start-capture", true, $"port={harness.Interception.BoundPort}");

        await harness.OnUiAsync(() => harness.Robot.Click("SystemProxyCheck")).ConfigureAwait(true);
        await harness.WaitUntilAsync(
            () => harness.ViewModel.SystemProxy && OsProxyStatus.WinInetPointsAt(harness.Interception.BoundPort),
            TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        await WaitForStatusAsync(harness, "System proxy enabled", TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        var on = await MeasureAsync(harness, log, "on").ConfigureAwait(true);
        log.Info(
            $"capture-on loopback={on.ProxyLoopback} override={on.Override} proxied={on.Proxied} direct={on.DirectHits} sessions={on.MatchingSessions}");

        if (!on.ProxyLoopback)
        {
            log.Step("capture-on", false, "Expected Capture local traffic to start checked");
            return 1;
        }

        await harness.OnUiAsync(() => harness.Robot.Click("MenuProxyLocalhost")).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => !harness.ViewModel.ProxyLoopback, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        await WaitForStatusAsync(harness, "localhost skips the system proxy", TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        var off = await MeasureAsync(harness, log, "off").ConfigureAwait(true);
        log.Info(
            $"capture-off loopback={off.ProxyLoopback} override={off.Override} proxied={off.Proxied} direct={off.DirectHits} sessions={off.MatchingSessions}");

        Measurement? restarted = null;
        try
        {
            await harness.OnUiAsync(() => harness.Robot.Click("MenuStopCapture")).ConfigureAwait(true);
            await WaitForStatusAsync(harness, "system proxy restored", TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => !harness.Interception.IsRunning, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
            await harness.WaitUntilAsync(
                () => harness.Interception.IsRunning
                      && harness.ViewModel.SystemProxy
                      && OsProxyStatus.WinInetPointsAt(harness.Interception.BoundPort),
                TimeSpan.FromSeconds(25)).ConfigureAwait(true);
            await WaitForStatusAsync(harness, "System proxy enabled", TimeSpan.FromSeconds(15)).ConfigureAwait(true);
            restarted = await MeasureAsync(harness, log, "restarted").ConfigureAwait(true);
            log.Info(
                $"capture-restarted loopback={restarted.Value.ProxyLoopback} override={restarted.Value.Override} proxied={restarted.Value.Proxied} direct={restarted.Value.DirectHits} sessions={restarted.Value.MatchingSessions}");
        }
        catch (Exception ex)
        {
            log.Info(
                $"capture-restart skipped: {ex.Message}; running={harness.Interception.IsRunning} systemProxy={harness.ViewModel.SystemProxy} status={harness.ViewModel.StatusText} os={OsProxyStatus.Dump()}");
        }

        var offStillProxies = off.Proxied || OverrideSendsLoopback(off.Override);
        var restartStillProxies = restarted is { } again && (again.Proxied || OverrideSendsLoopback(again.Override));
        log.Step(
            "capture-off-skips-loopback",
            !offStillProxies,
            offStillProxies
                ? $"Still proxying 127.0.0.1 after uncheck. override={off.Override}"
                : "Uncheck removed loopback capture");
        if (restarted is null)
        {
            log.Step("capture-restart-skips-loopback", false, "Restart measurement did not complete");
            return 1;
        }

        log.Step(
            "capture-restart-skips-loopback",
            !restartStillProxies,
            restartStillProxies
                ? $"Still proxying 127.0.0.1 after proxy restart. override={restarted.Value.Override}"
                : "Restart left loopback capture off");

        return offStillProxies || restartStillProxies ? 2 : 0;
    }

    private static bool OverrideSendsLoopback(string? proxyOverride) =>
        (proxyOverride ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(rule => rule.Equals("<-loopback>", StringComparison.OrdinalIgnoreCase));

    private static async Task WaitForStatusAsync(InspectorHarness harness, string fragment, TimeSpan timeout)
    {
        await harness.WaitUntilAsync(
            () => (harness.ViewModel.StatusText ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase),
            timeout).ConfigureAwait(true);
    }

    private static async Task<Measurement> MeasureAsync(InspectorHarness harness, ProbeLog log, string phase)
    {
        var marker = $"{ProbePath}-{phase}-{Guid.NewGuid():N}";
        using var origin = new DirectOrigin();
        var url = $"http://127.0.0.1:{origin.Port}{marker}";

        await harness.OnUiAsync(() =>
        {
            if (harness.ViewModel.Sessions.Count > 0 && harness.ViewModel.ClearSessionsCommand.CanExecute(null))
                harness.ViewModel.ClearSessionsCommand.Execute(null);
        }).ConfigureAwait(true);

        var winInetOk = WinInetFetch(url, out var winInetError);
        log.Info($"wininet {phase} ok={winInetOk} error={winInetError} url={url}");

        var matched = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            await harness.OnUiAsync(() =>
            {
                matched = harness.ViewModel.Sessions.Count(s =>
                    (s.Url ?? string.Empty).Contains(marker, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                       && (s.Url ?? string.Empty).Contains(origin.Port.ToString(), StringComparison.Ordinal));
            }).ConfigureAwait(true);
            if (matched > 0)
                break;
            await Task.Delay(100).ConfigureAwait(true);
        }

        var snapshot = OsProxyStatus.ReadWinInet();
        return new Measurement(
            harness.ViewModel.ProxyLoopback,
            snapshot.ProxyOverride,
            matched > 0,
            origin.DirectHits,
            matched);
    }

    private readonly record struct Measurement(
        bool ProxyLoopback,
        string? Override,
        bool Proxied,
        int DirectHits,
        int MatchingSessions);

    private static bool WinInetFetch(string url, out int error)
    {
        error = 0;
        var internet = InternetOpen("twp-capture-local-probe", 0, null, null, 0);
        if (internet == IntPtr.Zero)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            var timeout = 4000;
            InternetSetOption(internet, 2, ref timeout, sizeof(int));
            InternetSetOption(internet, 5, ref timeout, sizeof(int));
            InternetSetOption(internet, 6, ref timeout, sizeof(int));
            var handle = InternetOpenUrl(internet, url, null, 0, unchecked((int)0x80000000), IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            InternetCloseHandle(handle);
            return true;
        }
        finally
        {
            InternetCloseHandle(internet);
        }
    }

    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr InternetOpen(string agent, int accessType, string? proxy, string? bypass, int flags);

    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr InternetOpenUrl(
        IntPtr internet, string url, string? headers, int headersLength, int flags, IntPtr context);

    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, ref int value, int length);

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetCloseHandle(IntPtr handle);

    private sealed class DirectOrigin : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private int _directHits;

        public DirectOrigin()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(_cts.Token);
        }

        public int Port { get; }
        public int DirectHits => Volatile.Read(ref _directHits);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                Interlocked.Increment(ref _directHits);
                _ = ReplyAsync(client);
            }
        }

        private static async Task ReplyAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[1024];
                    using var read = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await stream.ReadAsync(buffer, read.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // client may close after headers
                    }

                    var payload = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    await stream.WriteAsync(payload).ConfigureAwait(false);
                }
                catch
                {
                    // probe only cares that a direct connection arrived
                }
            }
        }
    }
}
