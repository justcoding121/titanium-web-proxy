using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicConnectionCoreEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    [TestMethod]
    public void AddressAndConnectionIdEdgesStayInsideTheLength()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PathInfo([], 1, [1], 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PathInfo(new byte[17], 1, [1], 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PathInfo([1], 65536, [1], 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PathInfo([1], 1, [], 1));

        byte[] high = new byte[16];
        high[0] = 1;
        byte[] low = new byte[16];
        low[8] = 1;
        var left = new PathInfo(high, 65535, [9], 0);
        var right = new PathInfo(low, 65535, [9], 0);
        Assert.IsTrue(left.Equals(left));
        Assert.IsFalse(left.Equals(right));
        Assert.IsFalse(left.Equals(new PathInfo(high, 65534, [9], 0)));
        Assert.IsFalse(left.Equals(null));
        Assert.AreEqual(left.GetHashCode(), new PathInfo(high, 65535, [9], 0).GetHashCode());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuicConnectionId(new byte[21]));
        var empty = new QuicConnectionId([]);
        Assert.AreEqual(0, empty.Length);
        Assert.IsTrue(empty.Matches([]));
        Assert.IsFalse(empty.Matches([1]));
        byte[] full = new byte[20];
        full[8] = 2;
        full[19] = 3;
        var id = new QuicConnectionId(full);
        Assert.AreEqual(20, id.Length);
        Assert.IsTrue(id.Equals(new QuicConnectionId(full)));
        Assert.IsFalse(id.Equals(new QuicConnectionId(new byte[20])));
        Assert.IsFalse(id.Equals(null));
    }

    [TestMethod]
    public void TheLongerShortHeaderMatchAndATruncatedLongHeaderAreEdges()
    {
        var table = new QuicConnectionTable();
        var shorter = new QuicConnectionCore();
        var longer = new QuicConnectionCore();
        table.Add(new QuicConnectionId([0xaa]), shorter);
        table.Add(new QuicConnectionId([0xaa, 0xbb]), longer);

        Assert.IsFalse(table.TryDispatch(Now, [0xC0, 0, 0, 0, 0], Path));
        var tooLong = new byte[27];
        tooLong[0] = 0xC0;
        tooLong[5] = 21;
        Assert.IsFalse(table.TryDispatch(Now, tooLong, Path));
        var truncated = new byte[8];
        truncated[0] = 0xC0;
        truncated[5] = 4;
        Assert.IsFalse(table.TryDispatch(Now, truncated, Path));

        Assert.IsTrue(table.TryDispatch(Now, [0x40, 0xaa, 0xbb], Path));
        Assert.AreEqual(0L, shorter.BytesReceived(Path));
        Assert.AreEqual(3L, longer.BytesReceived(Path));
    }

    [TestMethod]
    public void AmplificationMtuAndPacingStopAtTheBoundary()
    {
        var core = new QuicConnectionCore();
        Assert.IsFalse(core.TryStageTransmit(Path, []));
        Assert.IsFalse(core.TryStageTransmit(Path, new byte[65536]));
        core.HandleDatagram(Now, new byte[100], Path);
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[301]));
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[300]));
        var buffer = new byte[65535];
        Assert.AreEqual(300, core.PollTransmit(Now, buffer, out PathInfo sent));
        Assert.AreEqual(Path, sent);
        Assert.AreEqual(0, core.PollTransmit(Now, buffer, out _));
        Assert.AreEqual(300L, core.BytesSent(Path));
        Assert.AreEqual(0L, core.BytesSent(new PathInfo([10, 0, 0, 9], 1, [10, 0, 0, 8], 2)));

        core.MarkAddressValidated(Path);
        Assert.IsTrue(core.IsPathValidated(Path));
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[800]));
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[100]));
        Assert.AreEqual(100, core.PollTransmit(Now, new byte[150], out _));
        Assert.AreEqual(0, core.PollTransmit(Now, new byte[150], out _));
        Assert.AreEqual(301, core.PollTransmit(Now, buffer, out _));
        Assert.AreEqual(800, core.PollTransmit(Now, buffer, out _));

        Assert.IsTrue(core.TryStageTransmit(Path, new byte[1201]));
        Assert.AreEqual(0, core.PollTransmit(Now, buffer, out _));
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[1201], pmtu: true));
        Assert.AreEqual(1201, core.PollTransmit(Now, buffer, out _));
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[65535], pmtu: true));
        Assert.AreEqual(65535, core.PollTransmit(Now, buffer, out _));

        for (int i = 0; i < 9; i++)
        {
            Assert.IsTrue(core.TryStageTransmit(Path, new byte[1200], track: true, number: (ulong)i, payload: [1]));
        }

        Assert.IsTrue(core.TryStageTransmit(Path, new byte[200], track: true, number: 9, payload: [1]));
        for (int i = 0; i < 10; i++)
        {
            Assert.IsTrue(core.PollTransmit(Now, buffer, out _) > 0);
        }

        Assert.AreEqual(11000L, core.BytesInFlight);
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[1200], track: true, number: 10, payload: [1]));
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[100], track: true, number: 11, payload: [1]));
        Assert.AreEqual(0, core.PollTransmit(Now, buffer, out _));
        Assert.AreEqual(Now + TimeSpan.FromMilliseconds(333), core.NextTimeout());
        Assert.IsFalse(core.HandleTimeout(Now + TimeSpan.FromMilliseconds(333)));
        Assert.AreEqual(Now + TimeSpan.FromMilliseconds(666), core.NextTimeout());

        Assert.IsTrue(core.TryStageTransmit(Path, new byte[100], track: true, probe: true, number: 12, payload: [1]));
        Assert.AreEqual(100, core.PollTransmit(Now, buffer, out _));
        long flight = core.BytesInFlight;
        Assert.IsTrue(core.TryStageTransmit(Path, new byte[50], track: true, exempt: true, number: 13, payload: [1]));
        Assert.AreEqual(50, core.PollTransmit(Now, buffer, out _));
        Assert.AreEqual(flight, core.BytesInFlight);

        var migrated = new PathInfo([10, 0, 0, 4], 9, [10, 0, 0, 5], 10);
        core.Migrate(migrated);
        Assert.IsFalse(core.IsPathValidated(migrated));
        core.MarkAddressValidated(migrated);
        Assert.IsTrue(core.IsPathValidated(migrated));
        core.MarkAddressValidated(migrated);
        var unseen = new PathInfo([1, 2, 3, 4], 7, [5, 6, 7, 8], 8);
        core.MarkAddressValidated(unseen);
        Assert.IsTrue(core.IsPathValidated(unseen));
        var missing = new PathInfo([9], 1, [8], 2);
        Assert.IsTrue(core.TryStageTransmit(missing, new byte[20]));
        Assert.AreEqual(0, core.PollTransmit(Now, buffer, out _));
    }

    [TestMethod]
    public void AnIdleDeadlineClosesBeforeTheLossTimer()
    {
        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        var core = new QuicConnectionCore();
        byte[] id = QuicConnectionSession.NewConnectionId();
        var parameters = new QuicTransportParameters
        {
            InitialSourceConnectionId = id,
            InitialMaxData = 1 << 20,
            InitialMaxStreamDataBidiLocal = 1 << 20,
            InitialMaxStreamDataBidiRemote = 1 << 20,
            InitialMaxStreamDataUni = 1 << 20,
            InitialMaxStreamsBidi = 4,
            InitialMaxStreamsUni = 4,
            MaxIdleTimeout = 1,
        };
        byte[] encoded = new byte[512];
        int written = QuicTransportParametersCodec.Write(parameters, encoded);
        core.StartClient(QuicConnectionSession.PathOf(local, remote), id, id, encoded.AsSpan(0, written));
        var buffer = new byte[8192];
        Assert.IsTrue(core.PollTransmit(Now, buffer, out _) > 0);
        Assert.IsFalse(core.IdleTimedOut);
        Assert.IsTrue(core.HandleTimeout(Now.AddHours(1)));
        Assert.IsTrue(core.IdleTimedOut);
        Assert.IsTrue(core.IsClosed);
        Assert.IsTrue(core.TryStageTransmit(QuicConnectionSession.PathOf(local, remote), [1, 2, 3]));
        Assert.AreEqual(0, core.PollTransmit(Now.AddHours(1), buffer, out _));

        var loss = new QuicConnectionCore();
        byte[] lossId = QuicConnectionSession.NewConnectionId();
        loss.StartClient(QuicConnectionSession.PathOf(local, remote), lossId, lossId, QuicConnectionSession.Parameters(client: true, lossId));
        Assert.IsTrue(loss.PollTransmit(Now, buffer, out _) > 0);
        Assert.IsTrue(loss.HandleTimeout(Now.AddHours(1)));
        Assert.IsFalse(loss.IdleTimedOut);
    }
}
