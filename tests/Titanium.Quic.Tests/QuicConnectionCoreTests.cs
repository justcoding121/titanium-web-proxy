using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicConnectionCoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    [TestMethod]
    public void AnUnknownDestinationConnectionIdDoesNotAllocate()
    {
        var table = new QuicConnectionTable();
        var core = new QuicConnectionCore();
        table.Add(new QuicConnectionId([0x01, 0x02]), core);
        Assert.IsFalse(table.TryDispatch(Now, Datagram([0x99], 40), Path));
        Assert.IsFalse(table.TryDispatch(Now, [0x40, 0x03, 0x04], Path));
        Assert.IsFalse(table.TryDispatch(Now, [], Path));
        Assert.AreEqual(1, table.Count);
        Assert.AreEqual(0L, core.BytesReceived(Path));

        Assert.IsTrue(table.TryDispatch(Now, Datagram([0x01, 0x02], 40), Path));
        Assert.AreEqual(1, table.Count);
        Assert.AreEqual(40L, core.BytesReceived(Path));
    }

    [TestMethod]
    public void ASmallDatagramProducesNoTransmitOverTheLimit()
    {
        var core = new QuicConnectionCore();
        core.HandleDatagram(Now, Datagram([0x01], 50), Path);
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[1200]));
        var buffer = new byte[2048];
        Assert.AreEqual(0, core.PollTransmit(Now, buffer, out _));
        Assert.AreEqual(0L, core.BytesSent(Path));

        core.HandleDatagram(Now, Datagram([0x01], 350), Path);
        Assert.AreEqual(400L, core.BytesReceived(Path));
        Assert.AreEqual(1200, core.PollTransmit(Now, buffer, out PathInfo sent));
        Assert.AreEqual(Path, sent);
        Assert.AreEqual(1200L, core.BytesSent(Path));

        Assert.IsTrue(core.TryStageTransmit(Path, new byte[1200]));
        Assert.AreEqual(0, core.PollTransmit(Now, buffer, out _));
        Assert.AreEqual(1200L, core.BytesSent(Path));

        core.MarkAddressValidated(Path);
        Assert.AreEqual(1200, core.PollTransmit(Now, buffer, out _));
        Assert.AreEqual(2400L, core.BytesSent(Path));
    }

    [TestMethod]
    public void AmplificationIsPerPath()
    {
        var other = new PathInfo([10, 0, 0, 1], 443, [10, 0, 0, 3], 50001);
        var core = new QuicConnectionCore();
        core.HandleDatagram(Now, Datagram([0x01], 50), Path);
        core.HandleDatagram(Now, Datagram([0x01], 200), other);
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[200]));
        Assert.IsTrue(core.TryStageTransmit(other, new byte[200]));
        var buffer = new byte[200];
        Assert.AreEqual(200, core.PollTransmit(Now, buffer, out PathInfo sent));
        Assert.AreEqual(other, sent);
        Assert.AreEqual(0, core.PollTransmit(Now, buffer, out _));
        Assert.AreEqual(0L, core.BytesSent(Path));
        Assert.AreEqual(200L, core.BytesSent(other));
    }

    [TestMethod]
    public void HandleTimeoutFiresWithoutASocket()
    {
        var core = new QuicConnectionCore();
        Assert.IsNull(core.NextTimeout());
        core.HandleDatagram(Now, Datagram([0x01], 50), Path);
        Assert.AreEqual(Now + QuicConnectionCore.ScheduleHook, core.NextTimeout());
        Assert.IsFalse(core.HandleTimeout(Now));
        Assert.IsTrue(core.HandleTimeout(Now + QuicConnectionCore.ScheduleHook));
        Assert.IsNull(core.NextTimeout());
        Assert.IsFalse(core.HandleTimeout(Now + QuicConnectionCore.ScheduleHook));
    }

    private static byte[] Datagram(byte[] connectionId, int size)
    {
        var buffer = new byte[size];
        buffer[0] = 0xC0;
        buffer[4] = 1;
        buffer[5] = (byte)connectionId.Length;
        connectionId.CopyTo(buffer.AsSpan(6));
        return buffer;
    }
}
