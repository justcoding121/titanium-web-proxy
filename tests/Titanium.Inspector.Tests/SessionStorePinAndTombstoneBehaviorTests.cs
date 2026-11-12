using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Pin retention and option enforcement that must not hard-crash the Avalonia-backed testhost.
/// Disk-spill tombstone races that abort testhost are intentionally omitted.
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
