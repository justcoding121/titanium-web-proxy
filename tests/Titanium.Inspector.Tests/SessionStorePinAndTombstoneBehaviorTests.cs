using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Pin retention and selective-remove spill tombstones — paths that keep the Inspector from resurfacing deleted
/// sessions or evicting the row the user is inspecting.
/// </summary>
[TestClass]
public class SessionStorePinAndTombstoneBehaviorTests
{
    private static SessionSnapshot Row(long id, int bodyBytes = 64) =>
        new()
        {
            Id = id,
            Method = "GET",
            Url = $"https://example.com/{id}",
            StatusCode = 200,
            StartedUtc = DateTimeOffset.UtcNow.AddMilliseconds(id),
            RequestBodyBytes = new byte[bodyBytes],
            ResponseBodyBytes = new byte[bodyBytes],
            RequestBodyCapture = BodyCaptureState.Complete,
            ResponseBodyCapture = BodyCaptureState.Complete,
        };

    // Bug class: the pinned session is hard-evicted when MaxSessionsInMemory is exceeded, so the inspect pane blanks.
    [TestMethod]
    public void PinnedSession_SurvivesMaxSessionsEviction()
    {
        using var store = new SessionStore(new SessionStoreOptions
        {
            MaxSessionsInMemory = 3,
            SpillBodiesToDisk = false,
        });

        store.Add(Row(1));
        store.Add(Row(2));
        store.PinnedSessionId = 1;
        store.Add(Row(3));
        store.Add(Row(4));
        store.Add(Row(5));

        Assert.IsNotNull(store.TryGet(1), "pinned id 1 must stay in memory");
        Assert.AreEqual(3, store.Count);
        Assert.IsNull(store.TryGet(2));
        Assert.IsNotNull(store.TryGet(5));
    }

    // Bug class: clearing the pin leaves the previous selection's bodies stranded in RAM forever when already spilled.
    [TestMethod]
    public async Task ChangingPin_ClearsBodiesOfPreviousPinnedSessionAlreadyOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-pin-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 50,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 32L * 1024 * 1024,
                },
                dir);

            store.Add(Row(1, bodyBytes: 256));
            store.PinnedSessionId = 1;
            await store.FlushSpillAsync();
            var first = store.TryGet(1);
            Assert.IsNotNull(first);
            Assert.IsTrue(first!.BodiesOnDisk);

            // Re-hydrate RAM bodies (as the inspect pane would), then pin another row.
            first.RequestBodyBytes = new byte[256];
            first.ResponseBodyBytes = new byte[256];
            store.Add(Row(2, bodyBytes: 256));
            store.PinnedSessionId = 2;

            var after = store.TryGet(1);
            Assert.IsNotNull(after);
            Assert.IsTrue(after!.BodiesOnDisk);
            Assert.IsTrue(after.RequestBodyBytes is null || after.RequestBodyBytes.Length == 0);
            Assert.IsTrue(after.ResponseBodyBytes is null || after.ResponseBodyBytes.Length == 0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Bug class: Remove does not tombstone, so a late spill rewrite recreates the HAR the user just deleted.
    [TestMethod]
    public async Task Remove_TombsTheSessionSoALateSpillCannotRecreateIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-tomb-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 32L * 1024 * 1024,
                },
                dir);

            store.Add(Row(7, bodyBytes: 512));
            store.Remove([7]);
            await store.FlushSpillAsync();
            await store.FlushDiskCleanupAsync();

            Assert.IsNull(store.TryGet(7));
            var hars = Directory.EnumerateFiles(dir, "*.har", SearchOption.AllDirectories).ToList();
            Assert.AreEqual(0, hars.Count, "removed sessions must not leave a HAR behind after cleanup");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Bug class: ApplyOptions with zero/negative limits keeps the previous (or zero) caps and never enforces.
    [TestMethod]
    public void ApplyOptions_ZeroLimits_FallBackToSafeDefaultsAndEnforce()
    {
        using var store = new SessionStore(new SessionStoreOptions
        {
            MaxSessionsInMemory = 100,
            SpillBodiesToDisk = false,
        });

        for (var i = 1; i <= 20; i++)
        {
            store.Add(Row(i));
        }

        store.ApplyOptions(new SessionStoreOptions
        {
            MaxSessionsInMemory = 0,
            DiskCacheMaxBytes = 0,
            SpillBodiesToDisk = false,
        });

        Assert.AreEqual(10_000, store.Options.MaxSessionsInMemory);
        Assert.AreEqual(2L * 1024 * 1024 * 1024, store.Options.DiskCacheMaxBytes);
        Assert.AreEqual(20, store.Count);

        store.ApplyOptions(new SessionStoreOptions
        {
            MaxSessionsInMemory = 5,
            SpillBodiesToDisk = false,
        });
        Assert.AreEqual(5, store.Count);
        Assert.IsNotNull(store.TryGet(20));
        Assert.IsNull(store.TryGet(15));
    }

    // Bug class: Clear does not raise SessionsCleared (grid keeps stale rows) or leaves Count > 0.
    [TestMethod]
    public void Clear_RaisesSessionsClearedAndDropsEveryRow()
    {
        using var store = new SessionStore(new SessionStoreOptions
        {
            MaxSessionsInMemory = 50,
            SpillBodiesToDisk = false,
        });

        store.Add(Row(1));
        store.Add(Row(2));
        var cleared = 0;
        var removed = 0;
        store.SessionsCleared += () => cleared++;
        store.SessionsRemoved += list => removed += list.Count;

        Assert.AreEqual(0, store.Clear());
        Assert.AreEqual(0, store.Count);
        Assert.AreEqual(1, cleared);
        Assert.AreEqual(0, removed, "Clear must not raise SessionsRemoved (grid resets via SessionsCleared)");
    }
}
