using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicLossEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Payload = [0x01];

    [TestMethod]
    public void ASampleIgnoresTheDelayUntilTheHandshakeIsConfirmed()
    {
        var loss = new QuicLossRecovery(new QuicNewReno());
        loss.OnPacketSent(QuicPacketKind.OneRtt, 0, 1200, Now, Payload);
        loss.OnPacketSent(QuicPacketKind.OneRtt, 1, 0, Now, Payload);
        var one = Range(0);
        Assert.IsTrue(loss.OnAck(QuicPacketKind.OneRtt, 1, TimeSpan.FromSeconds(1), one, 1, Now + TimeSpan.FromMilliseconds(100)));
        Assert.AreEqual(QuicLossRecovery.InitialRtt, loss.SmoothedRtt);
        Assert.AreEqual(1, loss.InFlight);

        Assert.IsTrue(loss.OnAck(QuicPacketKind.OneRtt, 0, TimeSpan.FromSeconds(1), one, 1, Now + TimeSpan.FromMilliseconds(100)));
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), loss.SmoothedRtt);

        var again = new QuicLossRecovery(new QuicNewReno());
        again.HandshakeConfirmed = true;
        again.OnPacketSent(QuicPacketKind.OneRtt, 0, 1200, Now, Payload);
        Assert.IsTrue(again.OnAck(QuicPacketKind.OneRtt, 0, TimeSpan.Zero, one, 1, Now + TimeSpan.FromMilliseconds(50)));
        again.OnPacketSent(QuicPacketKind.OneRtt, 1, 1200, Now + TimeSpan.FromMilliseconds(50), Payload);
        Assert.IsTrue(again.OnAck(QuicPacketKind.OneRtt, 1, TimeSpan.FromMilliseconds(40), one, 1, Now + TimeSpan.FromMilliseconds(250)));
        Assert.AreEqual(TimeSpan.FromTicks(656250), again.SmoothedRtt);
    }

    [TestMethod]
    public void ThePacketThresholdAndAProbeAreExact()
    {
        var controller = new QuicNewReno();
        var loss = new QuicLossRecovery(controller);
        for (ulong number = 0; number < 4; number++)
        {
            loss.OnPacketSent(QuicPacketKind.OneRtt, number, 1200, Now, Payload);
        }

        Assert.IsTrue(loss.OnAck(QuicPacketKind.OneRtt, 3, TimeSpan.Zero, Range(0), 1, Now));
        Assert.AreEqual(1, loss.DeclaredLost);
        Assert.AreEqual(2, loss.InFlight);
        Assert.AreEqual(1, loss.Pending.Count);
        Assert.AreEqual(6600L, controller.CongestionWindow);

        var pmtu = new QuicLossRecovery(new QuicNewReno());
        pmtu.OnPacketSent(QuicPacketKind.OneRtt, 0, 1200, Now, [9], pmtu: true);
        pmtu.OnPacketSent(QuicPacketKind.OneRtt, 5, 1200, Now, [5]);
        pmtu.OnPacketSent(QuicPacketKind.OneRtt, 3, 1200, Now, [3]);
        Assert.AreEqual(2400L, pmtu.BytesInFlight);
        pmtu.OnTimeout(Now);
        Assert.AreEqual(1, pmtu.Pending.Count);
        CollectionAssert.AreEqual(new byte[] { 3 }, pmtu.Pending[0].Payload);

        Assert.IsTrue(pmtu.OnAck(QuicPacketKind.OneRtt, 3, TimeSpan.Zero, Range(0), 1, Now));
        CollectionAssert.AreEqual(new ulong[] { 0 }, pmtu.LostPmtu.ToArray());
        Assert.AreEqual(13200L, pmtu.Controller.CongestionWindow);

        var acked = new QuicLossRecovery(new QuicNewReno());
        acked.OnPacketSent(QuicPacketKind.OneRtt, 4, 1200, Now, [4], pmtu: true);
        Assert.AreEqual(0L, acked.BytesInFlight);
        Assert.IsTrue(acked.OnAck(QuicPacketKind.OneRtt, 4, TimeSpan.Zero, Range(0), 1, Now));
        CollectionAssert.AreEqual(new ulong[] { 4 }, acked.AckedPmtu.ToArray());
        Assert.AreEqual(12000L, acked.CongestionWindow);
    }

    [TestMethod]
    public void TheProbeTimeoutDoublesAndStopsAtTheShiftCap()
    {
        var loss = new QuicLossRecovery(new QuicNewReno());
        TimeSpan plain = loss.CurrentProbeTimeout();
        loss.HandshakeConfirmed = true;
        TimeSpan confirmed = loss.CurrentProbeTimeout();
        Assert.AreEqual(QuicLossRecovery.MaxAckDelay, confirmed - plain);
        loss.HandshakeConfirmed = false;

        loss.OnPacketSent(QuicPacketKind.Initial, 0, 1200, Now, Payload);
        Assert.AreEqual(Now + plain, loss.Timeout);
        loss.OnTimeout(Now + plain);
        Assert.AreEqual(1, loss.Pending.Count);
        Assert.AreEqual(Now + plain + TimeSpan.FromTicks(plain.Ticks * 2), loss.Timeout);

        DateTimeOffset clock = Now;
        for (int i = 0; i < 15; i++)
        {
            loss.OnTimeout(clock);
        }

        DateTimeOffset capped = loss.Timeout!.Value;
        loss.OnTimeout(clock);
        Assert.AreEqual(capped, loss.Timeout);

        var idle = new QuicLossRecovery(new QuicNewReno());
        idle.OnTimeout(Now);
        Assert.IsNull(idle.Timeout);
        Assert.AreEqual(0, idle.Pending.Count);
    }

    [TestMethod]
    public void RangesSpacesAndTheSendWindowStayInsideTheBounds()
    {
        var controller = new QuicNewReno();
        var loss = new QuicLossRecovery(controller);
        Assert.IsFalse(loss.OnAck(QuicPacketKind.OneRtt, 0, TimeSpan.Zero, Range(0), 0, Now));
        loss.OnPacketSent(QuicPacketKind.Initial, 0, 100, Now, Payload);
        loss.OnPacketSent(QuicPacketKind.Handshake, 0, 100, Now, Payload);
        loss.OnPacketSent(QuicPacketKind.OneRtt, 0, 100, Now, Payload);
        Assert.AreEqual(3, loss.InFlight);
        Assert.IsTrue(loss.OnAck(QuicPacketKind.Handshake, 0, TimeSpan.Zero, Range(0), 1, Now));
        Assert.AreEqual(2, loss.InFlight);
        loss.DiscardInitial();
        Assert.AreEqual(1, loss.InFlight);
        Assert.AreEqual(100L, loss.BytesInFlight);
        Assert.IsNotNull(loss.Timeout);

        var rejected = new QuicLossRecovery(new QuicNewReno());
        rejected.OnPacketSent(QuicPacketKind.OneRtt, 0, 1200, Now, Payload);
        rejected.OnPacketSent(QuicPacketKind.OneRtt, 2, 1200, Now, Payload);
        Assert.IsFalse(rejected.OnAck(QuicPacketKind.OneRtt, 2, TimeSpan.Zero, Range(1), 1, Now));
        var hole = new QuicAckRange[2];
        hole[0] = new QuicAckRange { Length = 0 };
        hole[1] = new QuicAckRange { Gap = 0, Length = 0 };
        var kept = new QuicLossRecovery(new QuicNewReno());
        kept.OnPacketSent(QuicPacketKind.OneRtt, 0, 1200, Now, Payload);
        kept.OnPacketSent(QuicPacketKind.OneRtt, 1, 1200, Now, Payload);
        kept.OnPacketSent(QuicPacketKind.OneRtt, 2, 1200, Now, Payload);
        Assert.IsTrue(kept.OnAck(QuicPacketKind.OneRtt, 2, TimeSpan.Zero, hole, 2, Now));
        Assert.AreEqual(1, kept.InFlight);

        var wide = new QuicLossRecovery(new QuicNewReno());
        for (ulong number = 0; number <= 64; number++)
        {
            wide.OnPacketSent(QuicPacketKind.OneRtt, number, 1, Now, Payload);
        }

        Assert.IsTrue(wide.OnAck(QuicPacketKind.OneRtt, 64, TimeSpan.Zero, Range(64), 1, Now));
        Assert.AreEqual(0, wide.InFlight);

        controller = new QuicNewReno();
        var paused = new QuicLossRecovery(controller);
        paused.OnPacketSent(QuicPacketKind.OneRtt, 0, 1200, Now, Payload);
        Assert.IsTrue(paused.OnAck(QuicPacketKind.OneRtt, 0, TimeSpan.Zero, Range(0), 1, Now, grow: false));
        Assert.AreEqual(12000L, controller.CongestionWindow);
        Assert.IsTrue(paused.OnAck(QuicPacketKind.OneRtt, 0, TimeSpan.Zero, Range(0), 1, Now));
        Assert.AreEqual(12000L, controller.CongestionWindow);

        var grown = new QuicNewReno();
        var credit = new QuicLossRecovery(grown);
        credit.OnPacketSent(QuicPacketKind.OneRtt, 0, 12000, Now, Payload);
        Assert.IsFalse(credit.Allows(1));
        Assert.IsTrue(credit.Allows(0));
        Assert.IsTrue(credit.OnAck(QuicPacketKind.OneRtt, 0, TimeSpan.Zero, Range(0), 1, Now));
        Assert.AreEqual(24000L, grown.CongestionWindow);
        Assert.IsTrue(credit.Allows(16384));
        Assert.IsFalse(credit.Allows(16385));
    }

    [TestMethod]
    public void ASecondRangeAndAScheduledLossStayInsideTheBounds()
    {
        var unsent = new QuicLossRecovery(new QuicNewReno());
        unsent.OnPacketSent(QuicPacketKind.OneRtt, 0, 100, Now, Payload);
        Assert.IsFalse(unsent.OnAck(QuicPacketKind.OneRtt, 5, TimeSpan.Zero, Range(0), 1, Now));

        var overflow = new QuicLossRecovery(new QuicNewReno());
        overflow.OnPacketSent(QuicPacketKind.OneRtt, 5, 100, Now, Payload);
        var tooFar = new QuicAckRange[2];
        tooFar[0] = new QuicAckRange { Length = 0 };
        tooFar[1] = new QuicAckRange { Gap = 10, Length = 0 };
        Assert.IsFalse(overflow.OnAck(QuicPacketKind.OneRtt, 5, TimeSpan.Zero, tooFar, 2, Now));
        tooFar[1] = new QuicAckRange { Gap = 0, Length = 4 };
        Assert.IsFalse(overflow.OnAck(QuicPacketKind.OneRtt, 5, TimeSpan.Zero, tooFar, 2, Now));
        tooFar[1] = new QuicAckRange { Gap = 0, Length = 0 };
        Assert.IsFalse(overflow.OnAck(QuicPacketKind.OneRtt, 5, TimeSpan.Zero, tooFar, 2, Now));

        var discarded = new QuicLossRecovery(new QuicNewReno());
        discarded.OnPacketSent(QuicPacketKind.Initial, 0, 100, Now, Payload);
        Assert.IsNotNull(discarded.Timeout);
        discarded.DiscardInitial();
        Assert.IsNull(discarded.Timeout);
        Assert.AreEqual(0L, discarded.BytesInFlight);

        var scheduled = new QuicLossRecovery(new QuicNewReno());
        for (ulong number = 0; number < 4; number++)
        {
            scheduled.OnPacketSent(QuicPacketKind.OneRtt, number, 100, Now, Payload);
        }

        Assert.IsTrue(scheduled.OnAck(QuicPacketKind.OneRtt, 3, TimeSpan.Zero, Range(0), 1, Now));
        scheduled.OnTimeout(Now + TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(3, scheduled.DeclaredLost);
        Assert.AreEqual(2, scheduled.Pending.Count);

        var early = new QuicLossRecovery(new QuicNewReno());
        for (ulong number = 0; number < 4; number++)
        {
            early.OnPacketSent(QuicPacketKind.OneRtt, number, 100, Now, Payload);
            early.OnPacketSent(QuicPacketKind.Initial, number, 100, Now + TimeSpan.FromHours(1), Payload);
        }

        Assert.IsTrue(early.OnAck(QuicPacketKind.OneRtt, 3, TimeSpan.Zero, Range(0), 1, Now));
        Assert.IsTrue(early.OnAck(QuicPacketKind.Initial, 3, TimeSpan.Zero, Range(0), 1, Now + TimeSpan.FromHours(1)));
        int before = early.DeclaredLost;
        early.OnTimeout(Now + TimeSpan.FromMilliseconds(1));
        Assert.IsTrue(early.DeclaredLost > before);

        var between = new QuicNewReno();
        var spaced = new QuicLossRecovery(between);
        DateTimeOffset sample = Now + TimeSpan.FromMilliseconds(50);
        spaced.OnPacketSent(QuicPacketKind.OneRtt, 0, 1200, Now, Payload);
        Assert.IsTrue(spaced.OnAck(QuicPacketKind.OneRtt, 0, TimeSpan.Zero, Range(0), 1, sample));
        DateTimeOffset first = sample + TimeSpan.FromMilliseconds(10);
        DateTimeOffset middle = first + TimeSpan.FromSeconds(5);
        DateTimeOffset later = first + TimeSpan.FromSeconds(10);
        spaced.OnPacketSent(QuicPacketKind.OneRtt, 1, 1200, first, Payload);
        spaced.OnPacketSent(QuicPacketKind.OneRtt, 2, 1200, middle, Payload);
        Assert.IsTrue(spaced.OnAck(QuicPacketKind.OneRtt, 2, TimeSpan.Zero, Range(0), 1, middle));
        for (ulong number = 3; number <= 6; number++)
        {
            spaced.OnPacketSent(QuicPacketKind.OneRtt, number, 1200, later, Payload);
        }

        Assert.IsTrue(spaced.OnAck(QuicPacketKind.OneRtt, 6, TimeSpan.Zero, Range(0), 1, later));
        Assert.AreNotEqual(between.MinimumWindow, between.CongestionWindow);

        var grown = new QuicNewReno();
        var wide = new QuicLossRecovery(grown);
        for (ulong number = 0; number <= 69; number++)
        {
            wide.OnPacketSent(QuicPacketKind.OneRtt, number, 1, Now, Payload);
        }

        var ranges = new QuicAckRange[2];
        ranges[0] = new QuicAckRange { Length = 63 };
        ranges[1] = new QuicAckRange { Gap = 0, Length = 4 };
        Assert.IsTrue(wide.OnAck(QuicPacketKind.OneRtt, 69, TimeSpan.Zero, ranges, 2, Now));
        Assert.AreEqual(0, wide.InFlight);
        Assert.AreEqual(1, wide.DeclaredLost);
        Assert.AreEqual(6034L, grown.CongestionWindow);
    }

    private static QuicAckRange[] Range(ulong length)
    {
        return [new QuicAckRange { Length = length }];
    }
}
