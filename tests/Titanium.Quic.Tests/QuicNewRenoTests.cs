using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicNewRenoTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void SlowStartLossAndRecoveryStayInsideTheWindow()
    {
        var reno = new QuicNewReno();
        Assert.AreEqual(12000, reno.CongestionWindow);
        Assert.AreEqual(2 * QuicNewReno.MaxDatagramSize, reno.MinimumWindow);
        reno.OnSent(1000);
        Assert.AreEqual(1000, reno.BytesInFlight);
        reno.OnAcked(1000, Now, Now, grow: true);
        Assert.AreEqual(0, reno.BytesInFlight);
        Assert.AreEqual(13000, reno.CongestionWindow);
        reno.OnAcked(100, Now, Now, grow: false);
        Assert.AreEqual(0, reno.BytesInFlight);
        Assert.AreEqual(13000, reno.CongestionWindow);

        reno.OnSent(12000);
        reno.OnLost(1200, Now, Now, persistentCongestion: false);
        Assert.AreEqual(6500, reno.CongestionWindow);
        reno.OnLost(100, Now, Now + TimeSpan.FromMilliseconds(1), persistentCongestion: false);
        Assert.AreEqual(6500, reno.CongestionWindow);
        reno.OnAcked(100, Now, Now + TimeSpan.FromMilliseconds(1), grow: true);
        Assert.AreEqual(6500, reno.CongestionWindow);

        DateTimeOffset later = Now + TimeSpan.FromMilliseconds(2);
        reno.OnAcked(1200, later, later, grow: true);
        long avoided = 6500 + (QuicNewReno.MaxDatagramSize * 1200L / 6500);
        Assert.AreEqual(avoided, reno.CongestionWindow);
        reno.OnLost(100000, Now, later, persistentCongestion: false);
        Assert.AreEqual(0, reno.BytesInFlight);
        Assert.AreEqual(avoided, reno.CongestionWindow);

        reno.OnLost(100, later, later, persistentCongestion: true);
        Assert.AreEqual(reno.MinimumWindow, reno.CongestionWindow);
        reno.OnDiscarded(20000);
        Assert.AreEqual(0, reno.BytesInFlight);
        Assert.AreEqual(reno.MinimumWindow, reno.CongestionWindow);
    }
}
