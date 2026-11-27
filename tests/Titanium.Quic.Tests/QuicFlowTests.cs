using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    private static readonly byte[] ClientId = [0xC1, 0xC1, 0xC1, 0xC1];

    private static readonly byte[] ServerId = [0x51, 0x51, 0x51, 0x51];

    [TestMethod]
    public void AStreamBlocksAtTheCreditAndResumes()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, 4));
        server.StartServer(ServerId, certificate, Parameters(client: false, 4));
        Pump(client, server);
        Assert.AreEqual(4, client.SendStream(0, [1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.IsTrue(client.StreamSendBlocked);
        Pump(client, server);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, server.ReceivedStream);
        server.GrantStreamCredit(0, 8);
        Pump(client, server);
        Assert.IsFalse(client.StreamSendBlocked);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, server.ReceivedStream);
    }

    [TestMethod]
    public void APeerThatExceedsTheCreditClosesTheConnection()
    {
        using X509Certificate2 certificate = Certificate();
        using var server = new QuicConnectionCore();
        server.StartServer(ServerId, certificate, Parameters(client: false, 4));
        Span<byte> frame = stackalloc byte[16];
        var stream = new QuicFrame
        {
            Kind = QuicFrameKind.Stream,
            StreamId = 0,
            HasLength = true,
            Data = [1, 2, 3, 4, 5],
        };
        int written = QuicFrameWriter.Write(in stream, default, frame);
        server.InjectApplicationPayload(frame[..written]);
        Assert.IsTrue(server.IsClosed);
        Assert.AreEqual(QuicFlow.FlowControlError, server.CloseError);
    }

    [TestMethod]
    public void AHugeReassemblyGapIsRejected()
    {
        using X509Certificate2 certificate = Certificate();
        using var server = new QuicConnectionCore();
        server.StartServer(ServerId, certificate, Parameters(client: false, 4));
        Span<byte> frame = stackalloc byte[16];
        byte[] one = [0x01];
        var crypto = new QuicFrame { Kind = QuicFrameKind.Crypto, Offset = 20_000, Data = one };
        int framed = QuicFrameWriter.Write(in crypto, default, frame);
        using var send = QuicPacketProtector.FromInitial(ServerId, client: true);
        byte[] packet = new byte[1500];
        int written = QuicPackets.SealLong(packet, QuicPacketKind.Initial, ServerId, ClientId, 0, frame[..framed], send, 1200);
        Assert.IsTrue(written > 0);
        server.HandleDatagram(Now, packet.AsSpan(0, written), Path);
        Assert.IsTrue(server.IsClosed);
        Assert.AreEqual(QuicFlow.CryptoBufferExceeded, server.CloseError);
    }

    [TestMethod]
    public void ResetAndStopSendingAreDeliveredOnce()
    {
        using X509Certificate2 certificate = Certificate();
        using var server = new QuicConnectionCore();
        server.StartServer(ServerId, certificate, Parameters(client: false, 4));
        Span<byte> frame = stackalloc byte[24];
        var reset = new QuicFrame { Kind = QuicFrameKind.ResetStream, StreamId = 0, ErrorCode = 1, Limit = 0 };
        int written = QuicFrameWriter.Write(in reset, default, frame);
        server.InjectApplicationPayload(frame[..written]);
        server.InjectApplicationPayload(frame[..written]);
        var stop = new QuicFrame { Kind = QuicFrameKind.StopSending, StreamId = 4, ErrorCode = 2 };
        written = QuicFrameWriter.Write(in stop, default, frame);
        server.InjectApplicationPayload(frame[..written]);
        server.InjectApplicationPayload(frame[..written]);
        Assert.AreEqual(1, server.ResetsDelivered);
        Assert.AreEqual(1, server.StopsDelivered);
        Assert.IsFalse(server.IsClosed);
    }

    private static void Pump(QuicConnectionCore left, QuicConnectionCore right)
    {
        byte[] buffer = new byte[16384];
        for (int i = 0; i < 16; i++)
        {
            int count = left.PollTransmit(Now, buffer, out PathInfo path);
            if (count > 0)
            {
                right.HandleDatagram(Now, buffer.AsSpan(0, count), path);
            }

            count = right.PollTransmit(Now, buffer, out path);
            if (count > 0)
            {
                left.HandleDatagram(Now, buffer.AsSpan(0, count), path);
            }
        }
    }

    private static byte[] Parameters(bool client, ulong credit)
    {
        var parameters = new QuicTransportParameters
        {
            InitialSourceConnectionId = client ? ClientId : ServerId,
            InitialMaxData = credit,
            InitialMaxStreamDataBidiLocal = credit,
            InitialMaxStreamDataBidiRemote = credit,
            InitialMaxStreamDataUni = credit,
            InitialMaxStreamsBidi = 4,
            InitialMaxStreamsUni = 4,
        };
        if (!client)
        {
            parameters.OriginalDestinationConnectionId = ServerId;
        }
        byte[] buffer = new byte[512];
        int written = QuicTransportParametersCodec.Write(parameters, buffer);
        return buffer[..written];
    }

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
