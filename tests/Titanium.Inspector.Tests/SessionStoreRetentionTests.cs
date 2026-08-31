using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

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
            RequestBodyBytes = bodyBytes > 0 ? new byte[bodyBytes] : null,
            ResponseBodyBytes = bodyBytes > 0 ? new byte[bodyBytes] : null,
            RequestBodyText = bodyBytes > 0 ? new string('a', Math.Min(bodyBytes, 64)) : null,
            ResponseBodyText = bodyBytes > 0 ? new string('b', Math.Min(bodyBytes, 64)) : null,
        };

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
                    HotBodySessions = 3,
                    SpillBodiesToDisk = false,
                    MaxCaptureBytesInMemory = long.MaxValue,
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
    public async Task HotWindow_SpillsBodies_AndReloadRestores()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    HotBodySessions = 2,
                    SpillBodiesToDisk = true,
                    MaxCaptureBytesInMemory = long.MaxValue,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                    DiskCacheMaxAgeDays = 1,
                },
                dir);

            var s1 = MakeSession(1, 200);
            var s2 = MakeSession(2, 200);
            var s3 = MakeSession(3, 200);
            store.Add(s1);
            store.Add(s2);
            store.Add(s3);

            Assert.IsTrue(s1.BodiesOnDisk, "Oldest should spill past hot window");
            Assert.IsNull(s1.RequestBodyBytes);
            Assert.IsFalse(s3.BodiesOnDisk, "Newest stays hot");
            Assert.IsNotNull(s3.ResponseBodyBytes);

            await store.FlushSpillAsync();
            Assert.IsTrue(File.Exists(Path.Combine(dir, "1.bin")));

            await store.EnsureBodiesLoadedAsync(s1);
            Assert.IsFalse(s1.BodiesOnDisk);
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
    public void ByteBudget_ForcesSpillThenEvict()
    {
        var dir = TempCacheDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    HotBodySessions = 100,
                    SpillBodiesToDisk = true,
                    // Each session ~ 200+200 bytes + text; keep budget tiny so spill then evict.
                    MaxCaptureBytesInMemory = 50,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                    DiskCacheMaxAgeDays = 1,
                },
                dir);

            var removed = new List<SessionSnapshot>();
            store.SessionsRemoved += list => removed.AddRange(list);

            store.Add(MakeSession(1, 200));
            store.Add(MakeSession(2, 200));
            store.Add(MakeSession(3, 200));

            // Bodies spilled so in-memory budget drops; if still over, oldest rows evict.
            Assert.IsTrue(store.Count <= 3);
            Assert.IsTrue(store.InMemoryBodyBytes <= 50 || store.SpilledCount > 0 || removed.Count > 0);
            Assert.IsTrue(store.SpilledCount > 0 || removed.Count > 0);
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
                    HotBodySessions = 1,
                    SpillBodiesToDisk = true,
                    MaxCaptureBytesInMemory = long.MaxValue,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                    DiskCacheMaxAgeDays = 1,
                },
                dir);

            store.Add(MakeSession(10, 128));
            store.Add(MakeSession(11, 128));
            Assert.IsTrue(store.TryGet(10)!.BodiesOnDisk);
            await store.FlushSpillAsync();
            Assert.IsTrue(File.Exists(Path.Combine(dir, "10.bin")));

            store.Clear();
            Assert.AreEqual(0, store.Count);
            Assert.IsFalse(File.Exists(Path.Combine(dir, "10.bin")));
            Assert.IsFalse(Directory.EnumerateFiles(dir, "*.bin").Any());
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
                    HotBodySessions = 2,
                    SpillBodiesToDisk = false,
                    MaxCaptureBytesInMemory = long.MaxValue,
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
    public async Task BufferBurst_ThenStoreEviction_DoesNotThrow()
    {
        var dir = TempCacheDir();
        try
        {
            using var registry = new SessionRegistry(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 5,
                    HotBodySessions = 2,
                    SpillBodiesToDisk = true,
                    MaxCaptureBytesInMemory = long.MaxValue,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                    DiskCacheMaxAgeDays = 1,
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
            Assert.IsFalse(opts.SpillBodiesToDisk);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
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
            // Best-effort temp cleanup.
        }
    }
}
