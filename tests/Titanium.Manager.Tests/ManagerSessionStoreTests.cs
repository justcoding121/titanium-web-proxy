using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Auth;
using Titanium.Manager.Ui;

namespace Titanium.Manager.Tests;

/// <summary>
///     Direct session-store behavior: idle expiry, hard cap eviction, CSRF compare, and id length limits.
///     Bug classes: expired cookies still accepted, CSRF length-oracle, session table unbounded growth.
/// </summary>
[TestClass]
public class ManagerSessionStoreTests
{
    [TestMethod]
    public void CreateFindTouchAndRemove_RoundTrip()
    {
        var store = new ManagerSessionStore();
        var id = store.Create(AdminRole.Admin, "ui:admin", out var session);
        Assert.AreEqual(1, store.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.CsrfToken));

        var found = store.Find(id, touch: true);
        Assert.IsNotNull(found);
        Assert.AreEqual(session.Actor, found!.Actor);
        Assert.IsTrue(ManagerSessionStore.CsrfMatches(found, session.CsrfToken));
        Assert.IsFalse(ManagerSessionStore.CsrfMatches(found, "wrong"));
        Assert.IsFalse(ManagerSessionStore.CsrfMatches(found, null));

        Assert.IsTrue(store.Remove(id));
        Assert.IsNull(store.Find(id, touch: false));
        Assert.IsFalse(store.Remove(id));
    }

    [TestMethod]
    public void Find_RejectsEmptyAndOverlongIds()
    {
        var store = new ManagerSessionStore();
        Assert.IsNull(store.Find(null, touch: false));
        Assert.IsNull(store.Find("", touch: false));
        Assert.IsNull(store.Find(new string('a', 129), touch: false));
        Assert.IsFalse(store.Remove(null));
        Assert.IsFalse(store.Remove(new string('b', 200)));
    }

    [TestMethod]
    public void IdleTimeout_EvictsStaleSessions()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new ManagerSessionStore(time, idleTimeout: TimeSpan.FromMinutes(5), maxSessions: 8);
        var id = store.Create(AdminRole.Admin, "ui:admin", out _);
        Assert.IsNotNull(store.Find(id, touch: false));

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.IsNull(store.Find(id, touch: false));
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public void MaxSessions_EvictsLeastRecentlyUsed()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new ManagerSessionStore(time, idleTimeout: TimeSpan.FromHours(1), maxSessions: 2);
        var first = store.Create(AdminRole.Admin, "a", out _);
        time.Advance(TimeSpan.FromSeconds(1));
        var second = store.Create(AdminRole.Admin, "b", out _);
        time.Advance(TimeSpan.FromSeconds(1));
        _ = store.Find(second, touch: true);
        time.Advance(TimeSpan.FromSeconds(1));
        var third = store.Create(AdminRole.Admin, "c", out _);

        Assert.IsNull(store.Find(first, touch: false), "least-recently-used session must be evicted");
        Assert.IsNotNull(store.Find(second, touch: false));
        Assert.IsNotNull(store.Find(third, touch: false));
        Assert.AreEqual(2, store.Count);
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
