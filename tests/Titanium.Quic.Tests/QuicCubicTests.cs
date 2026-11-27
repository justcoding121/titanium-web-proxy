using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicCubicTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ALossKeepsSeventyPercentOfTheBytesInFlight()
    {
        var cubic = new QuicCubic();
        cubic.OnSent(12000);
        cubic.OnLost(1200, Now, Now, persistentCongestion: false);
        Assert.AreEqual(8400, cubic.CongestionWindow);

        var reno = new QuicNewReno();
        reno.OnSent(12000);
        reno.OnLost(1200, Now, Now, persistentCongestion: false);
        Assert.IsTrue(cubic.CongestionWindow > reno.CongestionWindow);
    }

    [TestMethod]
    public void TheWindowGrowsTowardTheRememberedSize()
    {
        var cubic = new QuicCubic();
        cubic.OnSent(12000);
        cubic.OnLost(1200, Now, Now, persistentCongestion: false);
        DateTimeOffset ack = Now + TimeSpan.FromSeconds(2);
        cubic.OnAcked(1200, ack - TimeSpan.FromMilliseconds(10), ack, grow: true);
        Assert.IsTrue(cubic.CongestionWindow > 8400);
        Assert.IsTrue(cubic.CongestionWindow < 12000);
    }

    [TestMethod]
    public void PersistentCongestionUsesTheMinimumWindow()
    {
        var cubic = new QuicCubic();
        cubic.OnSent(12000);
        cubic.OnLost(1200, Now, Now, persistentCongestion: true);
        Assert.AreEqual(cubic.MinimumWindow, cubic.CongestionWindow);
    }
}
