using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicFlowEdgeTests
{
    [TestMethod]
    public void SendCreditStopsAtTheSmallerLimitAndResumes()
    {
        var flow = new QuicFlow();
        flow.SetLocal(Parameters(100, 100, 100, 100, 4, 4), client: true);
        flow.SetPeer(Parameters(2, 5, 5, 3, 1, 1));

        Assert.AreEqual(0, flow.Queue(0, [], out bool blocked));
        Assert.IsTrue(blocked);
        Assert.AreEqual(0, flow.Queue(4, [1], out blocked));
        Assert.IsTrue(blocked);
        Assert.AreEqual(0, flow.Queue(6, [1], out blocked));

        int sent = flow.Queue(0, [1, 2, 3, 4, 5], out blocked);
        Assert.AreEqual(2, sent);
        Assert.IsTrue(blocked);
        IReadOnlyList<Outgoing> ready = flow.TakeReady();
        Assert.AreEqual(1, ready.Count);
        Assert.AreEqual(0UL, ready[0].Offset);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, ready[0].Data);
        Assert.AreEqual(0, flow.TakeReady().Count);

        flow.RaiseSend(2, 0, 5);
        Assert.IsTrue(flow.SendBlocked);
        flow.RaiseSend(20, 0, 5);
        Assert.IsFalse(flow.SendBlocked);
        ready = flow.TakeReady();
        Assert.AreEqual(2UL, ready[0].Offset);
        CollectionAssert.AreEqual(new byte[] { 3, 4, 5 }, ready[0].Data);

        Assert.AreEqual(3, flow.Queue(1, [6, 7, 8], out _));
        Assert.AreEqual(3, flow.Queue(2, [9, 10, 11, 12], out blocked));
        Assert.IsTrue(blocked);
        ready = flow.TakeReady();
        CollectionAssert.AreEqual(new byte[] { 6, 7, 8 }, ready[0].Data);
        CollectionAssert.AreEqual(new byte[] { 9, 10, 11 }, ready[1].Data);

        var raised = new QuicFlow();
        raised.SetLocal(Parameters(100, 100, 100, 100, 4, 4), client: true);
        raised.SetPeer(Parameters(100, 100, 2, 0, 4, 4));
        Assert.AreEqual(2, raised.Queue(0, [1, 2, 3, 4], out bool still));
        Assert.IsTrue(still);
        raised.RaiseSend(100, 0, 2);
        Assert.IsTrue(raised.SendBlocked);
        raised.RaiseSend(100, 0, 4);
        Assert.IsFalse(raised.SendBlocked);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, raised.TakeReady()[0].Data);
        Assert.AreEqual(0, raised.Queue(2, [9], out still));
        Assert.IsTrue(still);
    }

    [TestMethod]
    public void ReceiveCreditRejectsAWrapAndASecondLimit()
    {
        var flow = new QuicFlow();
        flow.SetLocal(Parameters(100, 100, 100, 100, 1, 1), client: true);
        flow.SetPeer(Parameters(100, 100, 100, 100, 1, 1));

        Assert.IsFalse(flow.AcceptStream(4, 0, [1], out ulong error));
        Assert.AreEqual(QuicFlow.StreamLimitError, error);
        Assert.IsFalse(flow.AcceptStream(0, ulong.MaxValue, [1], out error));
        Assert.AreEqual(QuicFrameReader.ProtocolViolation, error);

        Assert.IsTrue(flow.AcceptStream(0, 2, [3, 4], out error));
        Assert.AreEqual(0UL, error);
        Assert.AreEqual(0, flow.Received.Length);
        Assert.IsTrue(flow.AcceptStream(0, 0, [1, 2], out _));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, flow.Received);
        Assert.IsTrue(flow.AcceptStream(0, 0, [1, 2], out _));
        Assert.AreEqual(4, flow.Received.Length);

        var limited = new QuicFlow();
        limited.SetLocal(Parameters(4, 2, 8, 8, 4, 4), client: true);
        limited.SetPeer(Parameters(4, 2, 8, 8, 4, 4));
        Assert.IsFalse(limited.AcceptStream(0, 0, [1, 2, 3], out error));
        Assert.AreEqual(QuicFlow.FlowControlError, error);
        Assert.IsTrue(limited.AcceptStream(0, 0, [1, 2], out _));
        Assert.IsFalse(limited.AcceptStream(1, 0, [5, 6, 7], out error));
        Assert.AreEqual(QuicFlow.FlowControlError, error);
        limited.Grant(1, 8, 4);
        Assert.IsFalse(limited.AcceptStream(1, 0, [5, 6, 7], out error));
        limited.Grant(1, 8, 10);
        Assert.IsTrue(limited.AcceptStream(1, 0, [5, 6, 7], out _));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 5, 6, 7 }, limited.Received);

        var granted = new QuicFlow();
        granted.SetLocal(Parameters(100, 2, 100, 100, 4, 4), client: true);
        granted.SetPeer(Parameters(100, 2, 100, 100, 4, 4));
        Assert.IsTrue(granted.AcceptStream(0, 0, [1, 2], out _));
        granted.Grant(0, 2, 100);
        Assert.IsFalse(granted.AcceptStream(0, 2, [3, 4], out error));
        Assert.AreEqual(QuicFlow.FlowControlError, error);
        granted.Grant(0, 4, 100);
        Assert.IsTrue(granted.AcceptStream(0, 2, [3, 4], out _));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, granted.Received);

        var wide = new QuicFlow();
        wide.SetLocal(Parameters(100000, 100000, 100000, 100000, 4, 4), client: true);
        wide.SetPeer(Parameters(100000, 100000, 100000, 100000, 4, 4));
        Assert.IsFalse(wide.AcceptStream(0, 16385, [1], out error));
        Assert.AreEqual(QuicFlow.FlowControlError, error);

        Assert.IsFalse(flow.NoteReset(0, 1, out error));
        Assert.AreEqual(QuicFlow.FinalSizeError, error);
        Assert.IsTrue(flow.NoteReset(0, 4, out _));
        Assert.AreEqual(1, flow.ResetsDelivered);
        Assert.IsTrue(flow.NoteReset(0, 4, out _));
        Assert.AreEqual(1, flow.ResetsDelivered);
        Assert.IsFalse(flow.NoteReset(0, 5, out error));
        Assert.AreEqual(QuicFlow.FinalSizeError, error);
        Assert.AreEqual(1, flow.ResetsDelivered);

        Assert.IsTrue(flow.NoteStopSending(2));
        Assert.IsTrue(flow.NoteStopSending(2));
        Assert.AreEqual(1, flow.StopsDelivered);

        flow.ReturnReady(0, 9, []);
        flow.ReturnReady(0, 9, [8]);
        IReadOnlyList<Outgoing> returned = flow.TakeReady();
        Assert.AreEqual(9UL, returned[0].Offset);
        CollectionAssert.AreEqual(new byte[] { 8 }, returned[0].Data);
    }

    [TestMethod]
    public void CryptoAndAGapReassembleInOrder()
    {
        var flow = new QuicFlow();
        Assert.IsTrue(flow.PushCrypto(QuicPacketKind.Initial, 0, [1], out byte[] ready, out _));
        CollectionAssert.AreEqual(new byte[] { 1 }, ready);
        Assert.IsTrue(flow.PushCrypto(QuicPacketKind.Handshake, 1, [3], out ready, out _));
        Assert.AreEqual(0, ready.Length);
        Assert.IsTrue(flow.PushCrypto(QuicPacketKind.Handshake, 0, [2], out ready, out _));
        CollectionAssert.AreEqual(new byte[] { 2, 3 }, ready);
        var bounded = new QuicFlow();
        Assert.IsTrue(bounded.PushCrypto(QuicPacketKind.Initial, 16384, [1], out _, out _));
        Assert.IsFalse(bounded.PushCrypto(QuicPacketKind.Initial, 16385, [1], out _, out ulong cryptoError));
        Assert.AreEqual(QuicFlow.CryptoBufferExceeded, cryptoError);

        var buffer = new QuicReassembly(8);
        Assert.IsTrue(buffer.Push(0, [], 99, out _, out _));
        Assert.IsFalse(buffer.Push(ulong.MaxValue, [1], 99, out _, out ulong error));
        Assert.AreEqual(QuicFrameReader.ProtocolViolation, error);
        Assert.IsFalse(buffer.Push(9, [1], 99, out _, out error));
        Assert.AreEqual(99UL, error);
        Assert.IsTrue(buffer.Push(2, [3, 4], 99, out ready, out _));
        Assert.AreEqual(0, ready.Length);
        Assert.IsTrue(buffer.Push(2, [3, 4], 99, out _, out _));
        Assert.IsTrue(buffer.Push(0, [1, 2], 99, out ready, out _));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, ready);
        Assert.IsTrue(buffer.Push(3, [4, 5, 6], 99, out ready, out _));
        CollectionAssert.AreEqual(new byte[] { 5, 6 }, ready);

        var held = new QuicReassembly(8);
        Assert.IsTrue(held.Push(1, new byte[4], 99, out _, out _));
        Assert.IsTrue(held.Push(5, new byte[4], 99, out _, out _));
        Assert.IsFalse(held.Push(6, [1], 99, out _, out error));
        Assert.AreEqual(99UL, error);

        var ahead = new QuicReassembly(32);
        Assert.IsTrue(ahead.Push(6, [7], 99, out _, out _));
        Assert.IsTrue(ahead.Push(2, [3, 4], 99, out _, out _));
        Assert.IsTrue(ahead.Push(0, [1, 2], 99, out ready, out _));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, ready);
        Assert.IsTrue(ahead.Push(4, [5, 6], 99, out ready, out _));
        CollectionAssert.AreEqual(new byte[] { 5, 6, 7 }, ready);
    }

    [TestMethod]
    public void AServerUsesTheOppositeStreamDirection()
    {
        var server = new QuicFlow();
        server.SetLocal(Parameters(20, 5, 2, 4, 2, 2), client: false);
        server.SetPeer(Parameters(20, 2, 5, 4, 2, 2));

        Assert.AreEqual(2, server.Queue(0, [1, 2, 3], out bool blocked));
        Assert.IsTrue(blocked);
        Assert.AreEqual(3, server.Queue(1, [4, 5, 6], out _));
        Assert.AreEqual(4, server.Queue(3, [7, 8, 9, 10, 11], out blocked));
        Assert.IsTrue(blocked);
        Assert.AreEqual(0, server.Queue(8, [1], out _));
        Assert.AreEqual(0, server.Queue(11, [1], out _));

        Assert.IsFalse(server.AcceptStream(0, 0, [1, 2, 3], out ulong error));
        Assert.AreEqual(QuicFlow.FlowControlError, error);
        Assert.IsTrue(server.AcceptStream(0, 0, [1, 2], out _));
        Assert.IsTrue(server.AcceptStream(1, 0, [3, 4, 5, 6, 7], out _));
        Assert.IsFalse(server.AcceptStream(3, 0, [1, 2, 3, 4, 5], out error));
        Assert.AreEqual(QuicFlow.FlowControlError, error);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, server.Received);
    }

    private static QuicTransportParameters Parameters(ulong maxData, ulong bidiLocal, ulong bidiRemote, ulong uni, ulong maxBidi, ulong maxUni)
    {
        return new QuicTransportParameters
        {
            InitialMaxData = maxData,
            InitialMaxStreamDataBidiLocal = bidiLocal,
            InitialMaxStreamDataBidiRemote = bidiRemote,
            InitialMaxStreamDataUni = uni,
            InitialMaxStreamsBidi = maxBidi,
            InitialMaxStreamsUni = maxUni,
        };
    }
}
