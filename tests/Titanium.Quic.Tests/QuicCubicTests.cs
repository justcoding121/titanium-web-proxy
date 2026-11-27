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
        Assert.AreEqual(8914, cubic.CongestionWindow);
        DateTimeOffset again = ack + TimeSpan.FromSeconds(1);
        cubic.OnAcked(1200, again - TimeSpan.FromMilliseconds(50), again, grow: true);
        Assert.AreEqual(9406, cubic.CongestionWindow);
    }

    [TestMethod]
    public void PersistentCongestionUsesTheMinimumWindow()
    {
        var cubic = new QuicCubic();
        cubic.OnSent(12000);
        cubic.OnLost(1200, Now, Now, persistentCongestion: true);
        Assert.AreEqual(cubic.MinimumWindow, cubic.CongestionWindow);
        DateTimeOffset after = Now + TimeSpan.FromMilliseconds(5);
        cubic.OnAcked(1200, after, after, grow: true);
        Assert.AreEqual(cubic.MinimumWindow + 1200, cubic.CongestionWindow);
        cubic.OnDiscarded(20000);
        Assert.AreEqual(0, cubic.BytesInFlight);
        Assert.AreEqual(cubic.MinimumWindow + 1200, cubic.CongestionWindow);
    }

    [TestMethod]
    public void SlowStartGrowsAndASecondLossInRecoveryDoesNot()
    {
        var cubic = new QuicCubic();
        cubic.OnSent(1000);
        cubic.OnAcked(1000, Now, Now, grow: true);
        Assert.AreEqual(0, cubic.BytesInFlight);
        Assert.AreEqual(13000, cubic.CongestionWindow);
        cubic.OnAcked(50, Now, Now, grow: false);
        Assert.AreEqual(13000, cubic.CongestionWindow);

        cubic.OnSent(12000);
        cubic.OnLost(1200, Now, Now, persistentCongestion: false);
        Assert.AreEqual(8400, cubic.CongestionWindow);
        cubic.OnLost(100, Now, Now + TimeSpan.FromMilliseconds(1), persistentCongestion: false);
        Assert.AreEqual(8400, cubic.CongestionWindow);
        cubic.OnAcked(100, Now, Now + TimeSpan.FromMilliseconds(1), grow: true);
        Assert.AreEqual(8400, cubic.CongestionWindow);
        cubic.OnLost(100000, Now, Now + TimeSpan.FromMilliseconds(2), persistentCongestion: false);
        Assert.AreEqual(0, cubic.BytesInFlight);
        Assert.AreEqual(8400, cubic.CongestionWindow);
    }
}
