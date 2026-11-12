using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Api;

namespace Titanium.Manager.Tests;

/// <summary>
///     ConfigWaitHub notify / timeout leftovers.
///     Bug classes: notify never wakes waiters, timeout leaves orphans, other fleets are disturbed.
/// </summary>
[TestClass]
public class ConfigWaitHubBehaviorTests
{
    [TestMethod]
    public async Task NotifyFleet_WakesOnlyThatFleet()
    {
        var hub = new ConfigWaitHub();
        var a = hub.WaitAsync(1, TimeSpan.FromSeconds(30), CancellationToken.None);
        var b = hub.WaitAsync(2, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(30);
        Assert.AreEqual(1, hub.CountWaiters(1));
        Assert.AreEqual(1, hub.CountWaiters(2));

        hub.NotifyFleet(1);
        await a.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, hub.CountWaiters(1));
        Assert.AreEqual(1, hub.CountWaiters(2));

        hub.NotifyFleet(99);
        Assert.AreEqual(1, hub.CountWaiters(2));
        hub.NotifyFleet(2);
        await b.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, hub.CountWaiters(2));
    }

    [TestMethod]
    public async Task WaitAsync_Timeout_RemovesWaiterWithoutThrowingToCaller()
    {
        var hub = new ConfigWaitHub();
        await hub.WaitAsync(7, TimeSpan.FromMilliseconds(40), CancellationToken.None);
        Assert.AreEqual(0, hub.CountWaiters(7));
    }
}
