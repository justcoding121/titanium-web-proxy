using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
/// F01 leftover regression guard: a plain reverse configuration must not load assemblies that belong to
/// optional leftovers (GeoIP, xDS, DNS SRV, zstd) and must not start extra background work. Each later
/// feature extends <see cref="ForbiddenAssemblyNameFragments"/> and the task/thread snapshot as it lands.
/// Process-isolated from tests that deliberately load those assemblies.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DefaultConfigLoadsNothingNewTests
{
    /// <summary>Assembly name fragments that must stay unloaded on a default reverse config.</summary>
    private static readonly string[] ForbiddenAssemblyNameFragments =
    [
        "MaxMind.Db",
        "MaxMind.GeoIP2",
        "DnsClient",
        "ZstdSharp",
        "Grpc.Net.Client",
        "Google.Protobuf",
        "Titanium.Xds",
    ];

    [TestMethod]
    [Timeout(120_000)]
    public async Task DefaultReverseConfig_ServesTraffic_WithoutLoadingLeftoverAssemblies()
    {
        using var origin = new TinyOrigin();
        var port = FreePort();
        var dir = Path.Combine(Path.GetTempPath(), "twp-f01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $$"""
            schemaVersion: "7.1"
            logging:
              enabled: false
              enableConsole: false
            listeners:
              - host: "127.0.0.1"
                port: {{port}}
                decryptSsl: false
                enableHttp2: false
            routes:
              - id: r1
                clusterId: c1
                match: { path: /, pathKind: Prefix }
            clusters:
              - id: c1
                destinations:
                  - id: d1
                    address: "127.0.0.1"
                    port: {{origin.Port}}
            """);

        var cwd = Directory.GetCurrentDirectory();
        using var stop = new CancellationTokenSource();
        try
        {
            var beforeThreads = SnapshotThreadCount();
            var beforeTasks = SnapshotIncompleteTaskCount();

            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: true, stop.Token);

            var served = false;
            for (var attempt = 0; attempt < 40 && !served; attempt++)
            {
                await Task.Delay(250);
                served = await TryGetAsync(port);
            }

            Assert.IsTrue(served, "the default reverse configuration never answered a request");
            AssertForbiddenAssembliesNotLoaded("after serving a request on a default reverse configuration");

            // Warm-up: allow settle, then the leftover features must not have added a standing thread or timer.
            await Task.Delay(500);
            var afterThreads = SnapshotThreadCount();
            var afterTasks = SnapshotIncompleteTaskCount();
            Assert.IsTrue(afterThreads <= beforeThreads + 8,
                $"unexpected thread growth after warm-up: before={beforeThreads}, after={afterThreads}");
            Assert.IsTrue(afterTasks <= beforeTasks + 32,
                $"unexpected incomplete-task growth after warm-up: before={beforeTasks}, after={afterTasks}");

            await stop.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
            AssertForbiddenAssembliesNotLoaded("after the proxy stopped");
        }
        finally
        {
            await stop.CancelAsync();
            Directory.SetCurrentDirectory(cwd);
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static void AssertForbiddenAssembliesNotLoaded(string when)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? a.FullName ?? string.Empty)
            .ToList();

        Assert.IsTrue(loaded.Any(n => n.Contains("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)),
            "the assembly list did not contain the runtime itself; the check below would be vacuous");

        foreach (var fragment in ForbiddenAssemblyNameFragments)
        {
            var hit = loaded.FirstOrDefault(n => n.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            Assert.IsNull(hit, $"{when}: leftover assembly '{hit}' is loaded (matched '{fragment}')");
        }
    }

    private static int SnapshotThreadCount()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.Threads.Count;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    private static int SnapshotIncompleteTaskCount()
    {
        // Best-effort: TaskScheduler has no public global count; use ThreadPool pending work as a proxy
        // for standing timers/background work that leftover features must not start when disabled.
        return (int)Math.Min(int.MaxValue, ThreadPool.PendingWorkItemCount);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> TryGetAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/");
            return response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == "ok";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>A loopback HTTP/1.1 origin that answers every request with 200 and the body "ok".</summary>
    private sealed class TinyOrigin : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public TinyOrigin()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var seen = new StringBuilder();
                    while (!_stop.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                        {
                            return;
                        }

                        seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        while (seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            var all = seen.ToString();
                            seen.Clear().Append(all[(all.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
                            await stream.WriteAsync(
                                Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Type: text/plain\r\n\r\nok"),
                                _stop.Token);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    // the client went away
                }
            }
        }
    }
}
