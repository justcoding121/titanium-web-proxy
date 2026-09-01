using System.Net;
using System.Net.Quic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Cross-platform retention stress: thousands of captures under tight budgets with spill-to-disk.
/// Requires MsQuic on Linux/macOS CI (ui-portable installs it); Windows uses in-box MsQuic.
/// </summary>
[TestClass]
[TestCategory("Inspector-Stress")]
public class SessionStoreStressTests
{
    private static string TempCacheDir() =>
        Path.Combine(Path.GetTempPath(), "twp-stress-cache-" + Guid.NewGuid().ToString("N"));

    private static SessionSnapshot MakeSession(long id, int bodyBytes) =>
        new()
        {
            Id = id,
            Method = "GET",
            Url = $"https://example.com/stress/{id}",
            RequestBodyBytes = new byte[bodyBytes],
            ResponseBodyBytes = new byte[bodyBytes],
            RequestBodyText = new string('a', Math.Min(bodyBytes, 64)),
            ResponseBodyText = new string('b', Math.Min(bodyBytes, 64)),
        };

    [TestMethod]
    public async Task ThousandsOfSessions_SpillEvict_UnderTightBudgets()
    {
        const int total = 3000;
        const int maxSessions = 500;
        const int hot = 100;
        const int bodyBytes = 2048;
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = maxSessions,
                    HotBodySessions = hot,
                    SpillBodiesToDisk = true,
                    MaxCaptureBytesInMemory = 2L * 1024 * 1024, // 2 MiB
                    DiskCacheMaxBytes = 256L * 1024 * 1024,
                    DiskCacheMaxAgeDays = 1,
                },
                dir);

            var published = 0;
            for (var i = 1; i <= total; i++)
            {
                store.Add(MakeSession(i, bodyBytes));
                published++;
            }

            Assert.AreEqual(total, published);
            Assert.IsTrue(store.Count <= maxSessions, $"Count {store.Count} should be <= {maxSessions}");
            Assert.IsNull(store.TryGet(1), "Oldest session should be hard-evicted");
            Assert.IsNotNull(store.TryGet(total), "Newest session should remain");

            await store.FlushSpillAsync();

            var onDisk = Directory.EnumerateFiles(dir, "*.bin").Any();
            Assert.IsTrue(onDisk || store.TryGet(total) is { BodiesOnDisk: false },
                "Expected spill files or newest still hot after flush");

            var newest = store.TryGet(total);
            Assert.IsNotNull(newest);
            store.PinnedSessionId = newest!.Id;
            await store.EnsureBodiesLoadedAsync(newest);
            Assert.IsNotNull(newest.ResponseBodyBytes);
            Assert.AreEqual(bodyBytes, newest.ResponseBodyBytes!.Length);

            // Pinned must survive another wave of adds.
            for (var i = total + 1; i <= total + maxSessions; i++)
            {
                store.Add(MakeSession(i, bodyBytes));
            }

            Assert.IsNotNull(store.TryGet(total), "Pinned newest-from-first-wave must not be evicted");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task HttpCaptureStress_ThroughInterception_WithSpillStore()
    {
        Assert.IsTrue(QuicListener.IsSupported,
            "QuicListener.IsSupported must be true (install libmsquic/MsQuic on Linux/macOS CI).");

        using var origin = new HttpListener();
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var originPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        origin.Prefixes.Add($"http://127.0.0.1:{originPort}/");
        origin.Start();
        var payload = new string('x', 1024);
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        using var originCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!originCts.IsCancellationRequested && origin.IsListening)
            {
                try
                {
                    var ctx = await origin.GetContextAsync().WaitAsync(originCts.Token);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = payloadBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(payloadBytes, originCts.Token);
                    ctx.Response.Close();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    return;
                }
            }
        }, originCts.Token);

        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 200,
                    HotBodySessions = 40,
                    SpillBodiesToDisk = true,
                    MaxCaptureBytesInMemory = 512 * 1024,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                    DiskCacheMaxAgeDays = 1,
                },
                dir);

            using var interception = new InterceptionService(new RecordingSystemProxyController());
            interception.SessionCaptured += (_, snap) => store.Add(snap);
            interception.SessionUpdated += (_, snap) => store.NotifyUpdated(snap);

            await interception.StartAsync(IPAddress.Loopback, 0);
            Assert.IsTrue(interception.IsRunning);
            Assert.AreEqual(InterceptionService.IsHttp3Supported, interception.Http3Enabled);
            Assert.IsTrue(InterceptionService.IsHttp3Supported);

            const int requests = 800;
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var tasks = Enumerable.Range(0, requests).Select(async i =>
            {
                using var resp = await http.GetAsync($"http://127.0.0.1:{originPort}/r/{i}");
                resp.EnsureSuccessStatusCode();
            });
            await Task.WhenAll(tasks);

            // Allow pipeline to drain into the store.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (store.Count < 1 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            await store.FlushSpillAsync();
            Assert.IsTrue(store.Count > 0, "Store should contain captured sessions");
            Assert.IsTrue(store.Count <= 200, $"Store count {store.Count} should respect MaxSessionsInMemory");
            interception.Stop();
        }
        finally
        {
            originCts.Cancel();
            try
            {
                origin.Stop();
                origin.Close();
            }
            catch
            {
                // ignore
            }

            TryDeleteDir(dir);
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }
}
