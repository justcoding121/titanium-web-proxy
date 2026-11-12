using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Store;

namespace Titanium.Manager.Tests;

/// <summary>
///     ManagerStore agent-token revoke and audit list leftovers on the fat store surface.
/// </summary>
[TestClass]
public class ManagerStoreTokenAuditBehaviorTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"twp-mgr-token-audit-{Guid.NewGuid():N}.db");

    [TestMethod]
    public void CreateRevokeAgentToken_AndFindAgentByToken()
    {
        using var store = new ManagerStore(TempDb());
        var fleet = store.CreateFleet("tok");
        var (row, plain) = store.CreateAgentToken(fleet.Id, "edge-1");
        Assert.IsNotNull(store.FindAgentByToken(plain));

        Assert.IsTrue(store.RevokeAgentToken(row.Id));
        Assert.IsFalse(store.RevokeAgentToken(row.Id), "second revoke is a no-op");
        Assert.IsNull(store.FindAgentByToken(plain));
        Assert.IsFalse(store.RevokeAgentToken(999_999));
    }

    [TestMethod]
    public void Audit_AndListAudit_ClampLimit_AndOrderNewestFirst()
    {
        using var store = new ManagerStore(TempDb());
        store.Audit("alice", "publish", "fleet/1", "ok", "first");
        store.Audit("bob", "pause", "rollout/2", "ok");
        store.Audit("carol", "resume", "rollout/2", "denied", "csrf");

        var all = store.ListAudit(0);
        Assert.AreEqual(1, all.Count, "limit clamps to at least 1");
        Assert.AreEqual("carol", all[0].Actor);

        var two = store.ListAudit(2);
        Assert.AreEqual(2, two.Count);
        Assert.AreEqual("carol", two[0].Actor);
        Assert.AreEqual("bob", two[1].Actor);
        Assert.IsNull(two[1].Detail);
        Assert.AreEqual("csrf", store.ListAudit(10).Single(a => a.Actor == "carol").Detail);
    }
}
