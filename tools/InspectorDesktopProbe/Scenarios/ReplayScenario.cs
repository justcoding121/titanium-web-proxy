using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Titanium.Inspector.DesktopProbe.Shared;
using Titanium.Inspector.Services;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>Local scratch: real Inspector window, Decrypt traffic on, mixed traffic for N minutes.</summary>
public static class ReplayScenario
{
    private static readonly string[] Sites =
    {
        "news.google.com","www.google.com","www.bing.com","www.wikipedia.org","en.wikipedia.org","www.bbc.com","www.cnn.com","www.reuters.com",
        "github.com","stackoverflow.com","www.microsoft.com","learn.microsoft.com","www.apple.com","www.mozilla.org","developer.mozilla.org",
        "www.cloudflare.com","www.nytimes.com","www.theguardian.com","www.python.org","www.rust-lang.org","go.dev","nodejs.org","dotnet.microsoft.com",
        "www.amazon.com","www.ebay.com","www.reddit.com","www.imdb.com","www.nasa.gov","www.nature.com","arxiv.org","www.npr.org",
        "duckduckgo.com","www.yahoo.com","www.linkedin.com","www.etsy.com","www.wordpress.com","httpbin.org","example.com","www.iana.org",
        "www.w3.org","www.ietf.org","curl.se","www.openssl.org","www.kernel.org"
    };

    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log, int minutes, string logPath)
    {
        var pacing = typeof(Titanium.Inspector.Views.MainWindow).Assembly
            .GetType("Titanium.Inspector.Services.CaptureUiGovernor")?
            .GetProperty("IsPacingActive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?
            .GetValue(null);
        log.Info("Capture UI pacing active: " + pacing);
        log.Info("Install CA shows an OS Trusted Root Yes/No prompt: click Yes.");
        await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        if (Environment.GetEnvironmentVariable("TWP_REPLAY_DEBUG") == "1")
        {
            await harness.OnUiAsync(() => harness.Interception.ConfigureLogging(new InspectorSettings
            {
                LoggingEnabled = true,
                LoggingEnableFile = true,
                LoggingMinimumLevel = "Debug",
                LoggingFilePath = logPath,
            })).ConfigureAwait(true);
            log.Info("Debug file logging -> " + logPath);
        }

        harness.Dialogs.InstallRootCaResult = true;
        await harness.OnUiAsync(() => harness.Robot.Click("MenuInstallCa")).ConfigureAwait(true);

        var deadline = DateTime.UtcNow.AddMinutes(5);
        bool Trusted() => harness.Interception.IsRootTrusted || harness.Interception.VerifyOsUserSslTrust()
            || (OperatingSystem.IsWindows() && RootTrustHelpers.IsTitaniumRootInCurrentUserStore(harness.Interception));
        while (!Trusted() && DateTime.UtcNow < deadline)
            await Task.Delay(500).ConfigureAwait(true);
        if (!Trusted())
        {
            log.Step("install-ca", false, "Root CA not trusted within 5 min (prompt unanswered?)");
            return 1;
        }

        log.Step("install-ca", true, "trusted");

        await harness.OnUiAsync(() => harness.ViewModel.DecryptHttps = true).ConfigureAwait(true);
        log.Info("Decrypt traffic may raise an OS certificate-install prompt: click Yes (waiting up to 5 min).");
        var decryptDeadline = DateTime.UtcNow.AddMinutes(5);
        var decryptOn = false;
        var retried = false;
        var lastStatus = "";
        while (!decryptOn && DateTime.UtcNow < decryptDeadline)
        {
            var status = "";
            await harness.OnUiAsync(() =>
            {
                decryptOn = harness.ViewModel.DecryptHttps;
                status = $"status='{harness.ViewModel.StatusText}' running={harness.Interception.IsRunning} " +
                         $"rootTrusted={harness.Interception.IsRootTrusted} check={harness.Robot.GetCheck("DecryptHttpsCheck")}";
            }).ConfigureAwait(true);
            if (status != lastStatus)
            {
                log.Info("decrypt wait: " + status);
                lastStatus = status;
            }

            await Task.Delay(500).ConfigureAwait(true);

            if (!decryptOn && DateTime.UtcNow > decryptDeadline.AddMinutes(-4.5) && !retried)
            {
                // The checkbox shows on but the model never flipped; drive the same command the menu uses.
                retried = true;
                log.Info("decrypt still off after 30s; invoking ToggleDecryptHttpsCommand");
                await harness.OnUiAsync(() =>
                {
                    if (harness.ViewModel.ToggleDecryptHttpsCommand.CanExecute(null))
                        harness.ViewModel.ToggleDecryptHttpsCommand.Execute(null);
                }).ConfigureAwait(true);
            }
        }

        if (!decryptOn)
        {
            log.Step("decrypt-on", false, "DecryptHttps never became true: " + lastStatus);
            return 1;
        }

        log.Step("decrypt-on", true, "DecryptHttps=true");
        // Robot.SetCheck only moves the checkbox glyph here; the model flips through the property setter.
        await harness.OnUiAsync(() => harness.ViewModel.SystemProxy = true).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.ViewModel.SystemProxy, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        log.Step("system-proxy-on", true, "SystemProxy=true");

        var port = harness.Interception.BoundPort;
        log.Info($"Inspector proxy on 127.0.0.1:{port}; replay for {minutes} min");
        var end = DateTime.UtcNow.AddMinutes(minutes);
        var ok = 0; var bad = 0; var aborts = 0;

        async Task Worker(int id)
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var rnd = new Random(id);
            while (DateTime.UtcNow < end)
            {
                var site = Sites[rnd.Next(Sites.Length)];
                var url = rnd.Next(12) switch
                {
                    0 => $"https://nonexistent-{rnd.Next(100000)}.invalid/",
                    1 => "https://127.0.0.1:1/",
                    _ => $"https://{site}/",
                };
                try
                {
                    using var r = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (rnd.Next(3) == 0) _ = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref ok);
                }
                catch { Interlocked.Increment(ref bad); }
                await Task.Delay(rnd.Next(50, 400)).ConfigureAwait(false);
            }
        }

        async Task PinnedAborts()
        {
            while (DateTime.UtcNow < end)
            {
                try
                {
                    using var c = new TcpClient();
                    await c.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                    var s = c.GetStream();
                    var req = Encoding.ASCII.GetBytes("CONNECT api2.cursor.sh:443 HTTP/1.1\r\nHost: api2.cursor.sh:443\r\n\r\n");
                    await s.WriteAsync(req).ConfigureAwait(false);
                    var buf = new byte[1024];
                    _ = await s.ReadAsync(buf).ConfigureAwait(false);
                    using var ssl = new System.Net.Security.SslStream(s, false);
                    var t = ssl.AuthenticateAsClientAsync("api2.cursor.sh");
                    await Task.Delay(5).ConfigureAwait(false);
                    c.Client.Close();
                    try { await t.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
                    Interlocked.Increment(ref aborts);
                }
                catch { }
                await Task.Delay(700).ConfigureAwait(false);
            }
        }

        // Local HTTPS origin + flood workers: the real-internet mix above is latency bound (~10 sessions/s);
        // a browser with many tabs produces hundreds per second.
        var floodCount = int.TryParse(Environment.GetEnvironmentVariable("TWP_REPLAY_FLOOD"), out var fc) ? fc : 24;
        var flood = 0L;
        using var origin = new TinyHttpsOrigin();
        var originPort = origin.Start();

        async Task Flood(int id)
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var n = 0;
            while (DateTime.UtcNow < end)
            {
                try
                {
                    using var r = await client.GetAsync($"https://127.0.0.1:{originPort}/f{id}/{n++}").ConfigureAwait(false);
                    _ = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref flood);
                }
                catch { }
            }
        }

        // UI responsiveness: how long a posted job waits behind the load. Input-priority approximates a click.
        var inputLatency = new List<double>();
        var normalLatency = new List<double>();
        var sessionsAtEnd = 0;
        var pendingInput = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
        async Task Sampler()
        {
            var seq = 0;
            while (DateTime.UtcNow < end)
            {
                // Fire an Input-priority probe every 100ms without waiting, so a starved UI thread
                // still yields one sample per interval (recorded when it finally runs).
                var id = ++seq;
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                pendingInput[id] = t0;
                _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    lock (inputLatency) inputLatency.Add(ms);
                    pendingInput.TryRemove(id, out _);
                }, Avalonia.Threading.DispatcherPriority.Input);

                var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                    lock (normalLatency) normalLatency.Add(ms);
                }, Avalonia.Threading.DispatcherPriority.Normal);
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        log.Info($"Priorities: Input={(int)Avalonia.Threading.DispatcherPriority.Input.Value} " +
                 $"Normal={(int)Avalonia.Threading.DispatcherPriority.Normal.Value} " +
                 $"Background={(int)Avalonia.Threading.DispatcherPriority.Background.Value}; flood workers={floodCount}");

        // Optional real-browser load: headless Edge/Chrome over every article link on news.google.com.
        var pagesLoaded = 0; var pagesFailed = 0; var linkCount = 0;
        async Task Browse()
        {
            var exe = BrowserPaths.FindEdge() ?? BrowserPaths.FindChrome();
            if (exe is null) { log.Warn("No Edge/Chrome for browse load"); return; }

            async Task<string> RunHeadless(string url, bool viaProxy, int budgetMs, int killAfterSec)
            {
                var dir = Path.Combine(Path.GetTempPath(), "twp-replay-" + Guid.NewGuid().ToString("N"));
                var psi = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                {
                    "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                    "--disable-quic", "--user-data-dir=" + dir, "--dump-dom",
                    "--virtual-time-budget=" + budgetMs,
                    viaProxy ? $"--proxy-server=http://127.0.0.1:{port}" : "--no-proxy-server",
                    url,
                }) psi.ArgumentList.Add(a);

                using var p = System.Diagnostics.Process.Start(psi)!;
                var stdout = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(killAfterSec));
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
                var text = await stdout.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                try { Directory.Delete(dir, true); } catch { }
                return text;
            }

            var home = await RunHeadless("https://news.google.com/", viaProxy: false, 10000, 60).ConfigureAwait(false);
            var links = System.Text.RegularExpressions.Regex
                .Matches(home, "href=\"\\./((?:read|articles|stories|topics)/[^\"]+)\"")
                .Select(m => "https://news.google.com/" + System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))
                .Distinct().ToList();
            linkCount = links.Count;
            log.Info($"news.google.com links found: {links.Count}");
            if (links.Count == 0) { links.Add("https://news.google.com/"); }

            var next = -1;
            async Task BrowseWorker()
            {
                while (DateTime.UtcNow < end)
                {
                    var url = links[(int)((uint)Interlocked.Increment(ref next) % (uint)links.Count)];
                    try
                    {
                        var dom = await RunHeadless(url, viaProxy: true, 20000, 45).ConfigureAwait(false);
                        if (dom.Length > 200) Interlocked.Increment(ref pagesLoaded); else Interlocked.Increment(ref pagesFailed);
                    }
                    catch { Interlocked.Increment(ref pagesFailed); }
                }
            }

            var browsers = int.TryParse(Environment.GetEnvironmentVariable("TWP_REPLAY_BROWSERS"), out var bc) ? bc : 6;
            await Task.WhenAll(Enumerable.Range(0, browsers).Select(_ => Task.Run(BrowseWorker))).ConfigureAwait(false);
        }

        var tasks = Enumerable.Range(1, 8).Select(i => Task.Run(() => Worker(i))).ToList();
        if (Environment.GetEnvironmentVariable("TWP_REPLAY_BROWSE") == "1")
            tasks.Add(Task.Run(Browse));
        tasks.AddRange(Enumerable.Range(1, floodCount).Select(i => Task.Run(() => Flood(i))));
        tasks.Add(Task.Run(PinnedAborts));
        tasks.Add(Task.Run(Sampler));
        await Task.WhenAll(tasks).ConfigureAwait(true);
        await harness.OnUiAsync(() => sessionsAtEnd = harness.ViewModel.Sessions.Count).ConfigureAwait(true);

        static string Stats(List<double> v)
        {
            double[] a;
            lock (v) a = v.OrderBy(x => x).ToArray();
            if (a.Length == 0) return "n=0";
            double P(double q) => a[Math.Min(a.Length - 1, (int)(q * a.Length))];
            return $"n={a.Length} p50={P(0.5):F0}ms p90={P(0.9):F0}ms p99={P(0.99):F0}ms max={a[^1]:F0}ms over500ms={a.Count(x => x > 500)}";
        }

        foreach (var kv in pendingInput)
            lock (inputLatency) inputLatency.Add(System.Diagnostics.Stopwatch.GetElapsedTime(kv.Value).TotalMilliseconds);
        log.Step("replay", true, $"ok={ok} bad={bad} pinnedAborts={aborts} flood={flood} gridRows={sessionsAtEnd} browserPages ok={pagesLoaded} fail={pagesFailed} links={linkCount}");
        await harness.OnUiAsync(() =>
        {
            var heights = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(harness.Window)
                .OfType<Avalonia.Controls.DataGridRow>().Select(r => Math.Round(r.Bounds.Height, 1)).Distinct().Take(5);
            log.Info("grid row heights: " + string.Join(",", heights));
        }).ConfigureAwait(true);
        log.Info("UI latency Input priority : " + Stats(inputLatency));
        log.Info("UI latency Normal priority: " + Stats(normalLatency));

        return 0;
    }
}
