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
            StatusCode = 200,
            RequestBodyBytes = new byte[bodyBytes],
            ResponseBodyBytes = new byte[bodyBytes],
            RequestBodyText = new string('a', Math.Min(bodyBytes, 64)),
            ResponseBodyText = new string('b', Math.Min(bodyBytes, 64)),
            RequestBodyCapture = BodyCaptureState.Complete,
            ResponseBodyCapture = BodyCaptureState.Complete,
        };

    [TestMethod]
    public async Task ThousandsOfSessions_SpillEvict_UnderTightBudgets()
    {
        const int total = 3000;
        const int maxSessions = 500;
        const int bodyBytes = 2048;
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = maxSessions,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 256L * 1024 * 1024,
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

            Assert.IsTrue(Directory.EnumerateFiles(dir, "*.json").Any(), "Expected spill files after flush");

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
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
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

    [TestMethod]
    public async Task LargeBodies_AfterSpillAndDeselect_InMemoryBodyBytesNearZero()
    {
        const int sessions = 80;
        const int bodyBytes = 512 * 1024;
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 10_000,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 2L * 1024 * 1024 * 1024,
                },
                dir);

            for (var i = 1; i <= sessions; i++)
            {
                store.Add(MakeSession(i, bodyBytes));
            }

            await store.FlushSpillAsync();

            // ~80MB of bodies must leave RAM after spill (headers remain).
            Assert.IsTrue(
                store.InMemoryBodyBytes < 256 * 1024,
                $"After spill expected <256KB in-memory bodies, got {store.InMemoryBodyBytes}");

            for (var i = 1; i <= sessions; i++)
            {
                var snap = store.TryGet(i)!;
                store.PinnedSessionId = i;
                await store.EnsureBodiesLoadedAsync(snap);
                Assert.IsNotNull(snap.ResponseBodyBytes);
                store.PinnedSessionId = null;
                Assert.IsNull(snap.ResponseBodyBytes, "Deselect must unload");
            }

            await store.FlushSpillAsync();
            Assert.IsTrue(
                store.InMemoryBodyBytes < 256 * 1024,
                $"After pin/unpin sweep expected <256KB bodies, got {store.InMemoryBodyBytes}");

            // Late NotifyUpdated must not re-attach payloads after spill.
            var last = store.TryGet(sessions)!;
            last.ResponseBodyBytes = new byte[bodyBytes];
            last.ResponseBodyText = new string('z', 1024);
            store.NotifyUpdated(last);
            Assert.IsNull(last.ResponseBodyBytes);
            Assert.IsTrue(store.InMemoryBodyBytes < 256 * 1024);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task LiveCapture_LargeResponses_WorkingSetStableAfterSpill()
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
        var payloadBytes = new byte[400 * 1024];
        payloadBytes.AsSpan().Fill(0x41);
        using var originCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!originCts.IsCancellationRequested && origin.IsListening)
            {
                try
                {
                    var ctx = await origin.GetContextAsync().WaitAsync(originCts.Token);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/octet-stream";
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
                    MaxSessionsInMemory = 500,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 512L * 1024 * 1024,
                },
                dir);

            using var interception = new InterceptionService(new RecordingSystemProxyController());
            interception.SessionCaptured += (_, snap) => store.Add(snap);
            interception.SessionUpdated += (_, snap) => store.NotifyUpdated(snap);

            await interception.StartAsync(IPAddress.Loopback, 0);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            var baselineWs = Environment.WorkingSet;

            const int requests = 120;
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            for (var i = 0; i < requests; i++)
            {
                using var resp = await http.GetAsync($"http://127.0.0.1:{originPort}/big/{i}");
                resp.EnsureSuccessStatusCode();
            }

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (store.Count < requests && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            await store.FlushSpillAsync();
            // Cycle selection like a user browsing the grid.
            foreach (var snap in store.Sessions.Take(40).ToList())
            {
                store.PinnedSessionId = snap.Id;
                await store.EnsureBodiesLoadedAsync(snap);
                store.PinnedSessionId = null;
            }

            await store.FlushSpillAsync();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            var afterWs = Environment.WorkingSet;

            Assert.IsTrue(store.Count >= requests / 2, $"Expected captures, got {store.Count}");
            Assert.IsTrue(
                store.InMemoryBodyBytes < 2L * 1024 * 1024,
                $"Payload RAM after spill/deselect should be low, got {store.InMemoryBodyBytes}");
            // Working set may not return to baseline (LOH / native), but must not hold ~120*400KB.
            var growth = afterWs - baselineWs;
            Assert.IsTrue(
                growth < 180L * 1024 * 1024,
                $"Working set grew {growth / (1024 * 1024)} MB (baseline {baselineWs / (1024 * 1024)} → {afterWs / (1024 * 1024)}); possible body leak");

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
