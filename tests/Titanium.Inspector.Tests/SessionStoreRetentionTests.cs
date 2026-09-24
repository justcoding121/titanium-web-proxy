using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionStoreRetentionTests
{
    private static string TempCacheDir() =>
        Path.Combine(Path.GetTempPath(), "twp-session-cache-" + Guid.NewGuid().ToString("N"));

    private static SessionSnapshot MakeSession(long id, int bodyBytes = 100) =>
        new()
        {
            Id = id,
            Method = "GET",
            Url = $"https://example.com/{id}",
            StatusCode = 200,
            RequestHeadersText = "Host: example.com\r\n",
            ResponseHeadersText = "Content-Type: text/plain\r\n",
            RequestBodyBytes = bodyBytes > 0 ? new byte[bodyBytes] : null,
            ResponseBodyBytes = bodyBytes > 0 ? new byte[bodyBytes] : null,
            RequestBodyText = bodyBytes > 0 ? new string('a', Math.Min(bodyBytes, 64)) : null,
            ResponseBodyText = bodyBytes > 0 ? new string('b', Math.Min(bodyBytes, 64)) : null,
            RequestBodyCapture = bodyBytes > 0 ? BodyCaptureState.Complete : BodyCaptureState.None,
            ResponseBodyCapture = bodyBytes > 0 ? BodyCaptureState.Complete : BodyCaptureState.None,
        };

    [TestMethod]
    public async Task DoesNotArchive_UntilStatusCodeExists()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            var pending = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/pending",
                RequestBodyBytes = new byte[32],
                RequestBodyText = "pending",
                StatusCode = null,
            };
            store.Add(pending);
            await store.FlushSpillAsync();
            Assert.IsFalse(pending.BodiesOnDisk);
            Assert.IsFalse(File.Exists(Path.Combine(dir, "1.har")));

            pending.StatusCode = 200;
            pending.ResponseBodyBytes = new byte[16];
            pending.ResponseBodyText = "ok";
            pending.ResponseBodyCapture = BodyCaptureState.Complete;
            store.NotifyUpdated(pending);
            await store.FlushSpillAsync();

            Assert.IsTrue(pending.BodiesOnDisk);
            Assert.IsTrue(File.Exists(Path.Combine(dir, "1.har")));
            Assert.IsNull(pending.RequestBodyBytes, "Bodies unload after archive");
            var har = File.ReadAllText(Path.Combine(dir, "1.har"));
            StringAssert.Contains(har, "\"log\"");
            StringAssert.Contains(har, "\"version\":\"1.2\"");
            StringAssert.Contains(har, "\"_inspector\"");
            StringAssert.Contains(har, "\"status\":200");
            StringAssert.Contains(har, "pending");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task MemoryEviction_RewritesFinalArchive_WithLatestHeaders()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 1,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            var first = MakeSession(1, 64);
            store.Add(first);
            await store.FlushSpillAsync();
            first.ResponseHeadersText = "X-Final: 1\r\n";
            first.DurationMs = 42;

            store.Add(MakeSession(2, 64));
            await store.FlushSpillAsync();

            Assert.IsNull(store.TryGet(1));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "1.har")));
            var json = File.ReadAllText(Path.Combine(dir, "1.har"));
            StringAssert.Contains(json, "X-Final");
            StringAssert.Contains(json, "42");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public void MaxSessions_EvictsOldest()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 3,
                    SpillBodiesToDisk = false,
                },
                dir);

            var removed = new List<SessionSnapshot>();
            store.SessionsRemoved += list => removed.AddRange(list);

            store.Add(MakeSession(1));
            store.Add(MakeSession(2));
            store.Add(MakeSession(3));
            Assert.AreEqual(3, store.Count);

            store.Add(MakeSession(4));
            Assert.AreEqual(3, store.Count);
            Assert.IsNull(store.TryGet(1));
            Assert.IsNotNull(store.TryGet(4));
            Assert.AreEqual(1, removed.Count);
            Assert.AreEqual(1, removed[0].Id);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task FinishedSession_SpillsImmediately_AndReloadRestores()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            var s1 = MakeSession(1, 200);
            var s2 = MakeSession(2, 200);
            store.Add(s1);
            store.Add(s2);

            Assert.IsTrue(s1.BodiesOnDisk, "Finished session spills immediately");
            Assert.IsNull(s1.RequestBodyBytes);
            Assert.IsTrue(s2.BodiesOnDisk, "Newest finished session also spills");
            Assert.IsNull(s2.ResponseBodyBytes);

            await store.FlushSpillAsync();
            Assert.IsTrue(File.Exists(Path.Combine(dir, "1.har")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "2.har")));

            Assert.IsTrue(store.Options.SpillBodiesToDisk);
            Assert.IsTrue(
                File.ReadAllText(Path.Combine(dir, "1.har")).Contains("example.com", StringComparison.Ordinal),
                "Disk archive must include session headers/URL");

            await store.EnsureBodiesLoadedAsync(s1, CancellationToken.None);
            Assert.IsTrue(s1.BodiesOnDisk, "File remains; BodiesOnDisk stays true");
            Assert.IsNotNull(s1.RequestBodyBytes);
            Assert.AreEqual(200, s1.RequestBodyBytes!.Length);
            Assert.AreEqual(200, s1.ResponseBodyBytes!.Length);
            Assert.AreEqual(new string('a', 64), s1.RequestBodyText);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task PinnedSession_KeepsBodiesInRam_UnloadOnDeselect()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            store.PinnedSessionId = 1;
            var s1 = MakeSession(1, 200);
            store.Add(s1);
            Assert.IsTrue(s1.BodiesOnDisk);
            Assert.IsNotNull(s1.RequestBodyBytes, "Pinned session keeps RAM bodies after spill queue");

            await store.FlushSpillAsync();
            Assert.IsTrue(File.Exists(Path.Combine(dir, "1.har")));

            store.PinnedSessionId = null;
            Assert.IsNull(s1.RequestBodyBytes, "Deselect unloads RAM bodies when file exists");
            Assert.IsTrue(s1.BodiesOnDisk);

            await store.EnsureBodiesLoadedAsync(s1, CancellationToken.None);
            Assert.IsNotNull(s1.RequestBodyBytes);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task Deselect_UnloadsUpstreamAndProtobuf_ReloadsFromDisk()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            store.PinnedSessionId = 1;
            var s1 = MakeSession(1, 200);
            s1.UpstreamRequestBodyBytes = [9, 9, 9, 9];
            s1.ProtobufDecodedText = "field 1: hello";
            s1.GrpcFrames = [new GrpcFrameSnapshot { Length = 4, HexPreview = "09090909" }];
            store.Add(s1);
            await store.FlushSpillAsync();

            store.PinnedSessionId = null;
            Assert.IsNull(s1.RequestBodyBytes);
            Assert.IsNull(s1.UpstreamRequestBodyBytes);
            Assert.IsNull(s1.ProtobufDecodedText);
            Assert.IsNull(s1.GrpcFrames);

            await store.EnsureBodiesLoadedAsync(s1, CancellationToken.None);
            Assert.IsNotNull(s1.RequestBodyBytes);
            CollectionAssert.AreEqual(new byte[] { 9, 9, 9, 9 }, s1.UpstreamRequestBodyBytes);
            Assert.AreEqual("field 1: hello", s1.ProtobufDecodedText);
            Assert.IsNotNull(s1.GrpcFrames);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public void InFlightSession_DoesNotSpill()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            var s = MakeSession(1, 200);
            s.ResponseBodyStreamOpen = true;
            s.ResponseBodyCapture = BodyCaptureState.Streaming;
            store.Add(s);
            Assert.IsFalse(s.BodiesOnDisk);
            Assert.IsNotNull(s.ResponseBodyBytes);

            s.ResponseBodyStreamOpen = false;
            s.ResponseBodyCapture = BodyCaptureState.Complete;
            store.NotifyUpdated(s);
            Assert.IsTrue(s.BodiesOnDisk);
            Assert.IsNull(s.ResponseBodyBytes);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task Clear_DeletesSpillFiles()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            store.Add(MakeSession(10, 128));
            store.Add(MakeSession(11, 128));
            Assert.IsTrue(store.TryGet(10)!.BodiesOnDisk);
            await store.FlushSpillAsync();
            Assert.IsTrue(File.Exists(Path.Combine(dir, "10.har")));

            store.Clear();
            Assert.AreEqual(0, store.Count);
            Assert.IsFalse(File.Exists(Path.Combine(dir, "10.har")));
            Assert.IsFalse(Directory.EnumerateFiles(dir, "*.har").Any());
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public void PinnedSession_IsNotEvicted()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 2,
                    SpillBodiesToDisk = false,
                },
                dir);

            store.Add(MakeSession(1));
            store.Add(MakeSession(2));
            store.PinnedSessionId = 1;
            store.Add(MakeSession(3));

            Assert.IsNotNull(store.TryGet(1), "Pinned session must survive");
            Assert.IsNull(store.TryGet(2), "Unpinned oldest should go");
            Assert.IsNotNull(store.TryGet(3));
            Assert.AreEqual(2, store.Count);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task Eviction_LeavesSessionFileOnDisk()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 2,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            store.Add(MakeSession(1, 64));
            store.Add(MakeSession(2, 64));
            await store.FlushSpillAsync();
            Assert.IsTrue(File.Exists(Path.Combine(dir, "1.har")));

            store.Add(MakeSession(3, 64));
            Assert.IsNull(store.TryGet(1), "Memory eviction drops the row");
            Assert.IsTrue(File.Exists(Path.Combine(dir, "1.har")), "Disk archive stays until disk budget prunes");
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task DiskBudget_DeletesOldestSessionFiles_IndependentlyOfList()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            store.Add(MakeSession(1, 400));
            await store.FlushSpillAsync();
            var path1 = Path.Combine(dir, "1.har");
            Assert.IsTrue(File.Exists(path1));
            var size1 = new FileInfo(path1).Length;

            // Cap just above one file so the next archive forces a prune — independent of list size.
            store.ApplyOptions(new SessionStoreOptions
            {
                MaxSessionsInMemory = 100,
                SpillBodiesToDisk = true,
                DiskCacheMaxBytes = size1 + 256,
            });

            store.Add(MakeSession(2, 400));
            await store.FlushSpillAsync();

            Assert.AreEqual(2, store.Count, "Disk prune must not remove list rows");
            Assert.IsFalse(File.Exists(path1), "Oldest disk file pruned by size budget");
            Assert.IsTrue(File.Exists(Path.Combine(dir, "2.har")));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task BufferBurst_ThenStoreEviction_DoesNotThrow()
    {
        var dir = TempCacheDir();
        try
        {
            using var registry = new SessionRegistry(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 5,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);
            var buffer = new SessionStreamBuffer(registry, capacity: 100);
            var added = 0;
            var tcs = new TaskCompletionSource();
            buffer.SessionAdded += s =>
            {
                registry.Add(s);
                if (Interlocked.Increment(ref added) >= 20)
                {
                    tcs.TrySetResult();
                }
            };

            for (var i = 0; i < 20; i++)
            {
                var snap = buffer.CreatePlaceholder("GET", $"https://example.com/{i}");
                snap.ResponseBodyBytes = new byte[64];
                buffer.Publish(snap);
            }

            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(5, registry.Store.Count);
            await registry.Store.FlushSpillAsync();
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public void Settings_RoundTripsRetentionFields()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-retention-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new SettingsService(path);
            svc.Current.MaxSessionsInMemory = 1234;
            svc.Current.HotBodySessions = 56;
            svc.Current.SpillBodiesToDisk = false;
            svc.Current.MaxCaptureBytesInMemory = 99_000;
            svc.Current.DiskCacheMaxBytes = 1_000_000;
            svc.Current.DiskCacheMaxAgeDays = 3;
            svc.Save();

            var loaded = new SettingsService(path).Current;
            Assert.AreEqual(1234, loaded.MaxSessionsInMemory);
            Assert.AreEqual(56, loaded.HotBodySessions);
            Assert.IsFalse(loaded.SpillBodiesToDisk);
            Assert.AreEqual(99_000, loaded.MaxCaptureBytesInMemory);
            Assert.AreEqual(1_000_000, loaded.DiskCacheMaxBytes);
            Assert.AreEqual(3, loaded.DiskCacheMaxAgeDays);

            var opts = SessionStoreOptions.FromSettings(loaded);
            Assert.AreEqual(1234, opts.MaxSessionsInMemory);
            Assert.AreEqual(1_000_000, opts.DiskCacheMaxBytes);
            Assert.IsTrue(opts.SpillBodiesToDisk, "FromSettings always enables spill");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task EnsureBodiesLoadedAsync_CanceledToken_Throws()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            var spilled = MakeSession(2, 80);
            store.Add(spilled);
            Assert.IsTrue(spilled.BodiesOnDisk);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => store.EnsureBodiesLoadedAsync(spilled, cts.Token));
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => store.EnsureBodiesLoadedAsync(new[] { spilled }, cts.Token));

            await store.EnsureBodiesLoadedAsync(new[] { MakeSession(9, 0) }, CancellationToken.None);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task EnsureBodiesLoadedAsync_InMemorySnapshot_IsNoOpEvenWhenCanceled()
    {
        using var store = new SessionStore(
            new SessionStoreOptions
            {
                SpillBodiesToDisk = false,
                MaxSessionsInMemory = 10,
            });

        var snap = MakeSession(1, 32);
        store.Add(snap);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Bodies are already in memory — early return before the cancel check.
        await store.EnsureBodiesLoadedAsync(snap, cts.Token);
        Assert.IsFalse(snap.BodiesOnDisk);
        await store.EnsureBodiesLoadedAsync(new[] { snap }, CancellationToken.None);
    }

    [TestMethod]
    public async Task TryMatchBodySearch_ReadsSpilledTextWithoutHydrating()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            var s = MakeSession(1, 64);
            s.ResponseBodyText = "find-me-secret";
            store.Add(s);
            await store.FlushSpillAsync();
            Assert.IsNull(s.ResponseBodyText);

            Assert.IsTrue(store.TryMatchBodySearch(s, "find-me-secret"));
            Assert.IsNull(s.ResponseBodyText, "Search must not leave text on the snapshot");
            Assert.IsFalse(store.TryMatchBodySearch(s, "nope"));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task DiskBudget_MarksBodiesMissing_AndEnsureLoadIsFast()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            store.Add(MakeSession(1, 400));
            await store.FlushSpillAsync();
            var path1 = Path.Combine(dir, "1.har");
            Assert.IsTrue(File.Exists(path1));
            var size1 = new FileInfo(path1).Length;

            store.ApplyOptions(new SessionStoreOptions
            {
                MaxSessionsInMemory = 100,
                SpillBodiesToDisk = true,
                DiskCacheMaxBytes = size1 + 256,
            });

            store.Add(MakeSession(2, 400));
            await store.FlushSpillAsync();
            Assert.IsFalse(File.Exists(path1));

            var s1 = store.TryGet(1)!;
            Assert.IsTrue(s1.BodiesMissingFromDisk, "Prune should mark the live session");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await store.EnsureBodiesLoadedAsync(s1, CancellationToken.None);
            sw.Stop();
            Assert.IsTrue(sw.ElapsedMilliseconds < 200, "Missing body must not poll for ~1s");
            Assert.IsNull(s1.RequestBodyBytes);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public void BuildBodyCaptureHint_ExplainsDiskPrunedBody()
    {
        var hint = typeof(MainWindowViewModel).GetMethod(
            "BuildBodyCaptureHint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var snap = new SessionSnapshot
        {
            BodiesOnDisk = true,
            BodiesMissingFromDisk = true,
            ResponseBodyCapture = BodyCaptureState.Complete,
            BodySize = 12_000,
        };
        var text = (string)hint.Invoke(null, [snap])!;
        StringAssert.Contains(text, "disk cache limit");
        StringAssert.Contains(text, "Headers in the list are still available");
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
            // Best-effort temp cleanup.
        }
    }
}
