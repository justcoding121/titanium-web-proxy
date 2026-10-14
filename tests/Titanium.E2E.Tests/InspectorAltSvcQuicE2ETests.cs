using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.DesktopProbe.Shared;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests;

/// <summary>
/// A browser behind an explicit HTTP proxy cannot send UDP. If it honoured an origin
/// <c>Alt-Svc: h3</c> header, later requests would leave the proxy and never appear in the grid.
/// Local result (Chrome, Edge, Firefox, QUIC left on, loopback not bypassed): they stayed on the
/// proxy and sent no datagrams to the advertised port. HTTP/3 is off on the proxy here so its own
/// QUIC warmup is not counted. Safari is a manual check. Not run in PR CI (E2E-Slow).
/// </summary>
[TestClass]
public class InspectorAltSvcQuicE2ETests
{
    [TestMethod]
    [TestCategory("E2E-Slow")]
    public async Task Browsers_BehindExplicitProxy_DoNotOpenDirectQuicAfterAltSvc()
    {
        var browsers = new (string Name, string? Path)[]
        {
            ("chrome", BrowserPaths.FindChrome()),
            ("edge", BrowserPaths.FindEdge()),
            ("firefox", BrowserPaths.FindFirefox()),
        };
        var present = browsers.Where(b => b.Path is not null).ToArray();
        if (present.Length == 0)
        {
            Assert.Inconclusive("Chrome, Edge, and Firefox were not found.");
            return;
        }

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var quicPackets = 0;
        using var stop = new CancellationTokenSource();
        var listen = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await udp.ReceiveAsync(stop.Token);
                    Interlocked.Increment(ref quicPackets);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
            }
        }, stop.Token);

        using var origin = new HttpsEchoOrigin
        {
            ExtraResponseHeaders = $"Alt-Svc: h3=\":{udpPort}\"; ma=60",
        };
        using var interception = new InterceptionService
        {
            DecryptHttps = true,
            IgnoreServerCertificateErrors = true,
            UseInMemoryTrustState = true,
            // The proxy's own HTTP/3 warmup would send UDP to this Alt-Svc port and
            // look like a browser escape. This lab only counts browser datagrams.
            PreferHttp3 = false,
        };
        var captured = new List<string>();
        interception.SessionCaptured += (_, s) =>
        {
            if (s.Url.Contains("altsvc-probe", StringComparison.OrdinalIgnoreCase))
            {
                lock (captured)
                {
                    captured.Add(s.Method + " " + s.Url);
                }
            }
        };

        try
        {
            await interception.StartAsync(IPAddress.Loopback, 0);
            var notes = new StringBuilder();
            foreach (var (name, path) in present)
            {
                var hits = await VisitTwiceAsync(name, path!, interception.BoundPort, origin.Port);
                notes.Append(name).Append(" exits=").Append(hits).Append("; ");
            }

            await Task.Delay(500);
            int packets;
            int rows;
            lock (captured)
            {
                packets = quicPackets;
                rows = captured.Count;
            }

            Assert.AreEqual(0, packets,
                "A browser opened UDP toward the Alt-Svc port, skipping the proxy. " + notes);
            Assert.IsTrue(rows >= present.Length,
                "Expected each browser visit to stay on the proxy. Captured " + rows + ". " + notes);
        }
        finally
        {
            stop.Cancel();
            try { udp.Close(); } catch { /* ignore */ }
            try { await listen; } catch { /* ignore */ }
            try
            {
                interception.Stop();
                interception.EnsureShutdown();
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static async Task<string> VisitTwiceAsync(string name, string exe, int proxyPort, int originPort)
    {
        var userData = Path.Combine(Path.GetTempPath(), "twp-altsvc-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userData);
        if (name == "firefox")
        {
            File.WriteAllText(Path.Combine(userData, "user.js"),
                "user_pref(\"network.proxy.type\", 1);\n" +
                "user_pref(\"network.proxy.http\", \"127.0.0.1\");\n" +
                $"user_pref(\"network.proxy.http_port\", {proxyPort});\n" +
                "user_pref(\"network.proxy.ssl\", \"127.0.0.1\");\n" +
                $"user_pref(\"network.proxy.ssl_port\", {proxyPort});\n" +
                "user_pref(\"network.proxy.allow_hijacking_localhost\", true);\n" +
                "user_pref(\"network.http.http3.enable\", true);\n");
        }

        var url = $"https://127.0.0.1:{originPort}/altsvc-probe";
        try
        {
            var first = await RunBrowserAsync(name, exe, userData, proxyPort, url);
            await Task.Delay(1500);
            var second = await RunBrowserAsync(name, exe, userData, proxyPort, url);
            return first + "," + second;
        }
        finally
        {
            try
            {
                if (Directory.Exists(userData))
                {
                    Directory.Delete(userData, recursive: true);
                }
            }
            catch
            {
                // ignore a locked profile
            }
        }
    }

    private static async Task<int> RunBrowserAsync(string name, string exe, string userData, int proxyPort, string url)
    {
        var args = name == "firefox"
            ? $"-headless -no-remote -profile \"{userData}\" \"{url}\""
            : $"--headless=new --disable-gpu --no-first-run --disable-extensions " +
              $"--ignore-certificate-errors --user-data-dir=\"{userData}\" " +
              $"--proxy-server=\"http://127.0.0.1:{proxyPort}\" " +
              "--proxy-bypass-list=\"<-loopback>\" " +
              $"\"{url}\"";

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception)
        {
            return -3;
        }

        if (proc is null)
        {
            return -1;
        }

        using (proc)
        {
            _ = proc.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }

            return proc.HasExited ? proc.ExitCode : -2;
        }
    }
}
