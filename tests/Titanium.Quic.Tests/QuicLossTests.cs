using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicLossTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    private static readonly byte[] ClientId = [0xC1, 0xC1, 0xC1, 0xC1];

    private static readonly byte[] ServerId = [0x51, 0x51, 0x51, 0x51];

    [TestMethod]
    public void ADroppedPacketIsRetransmitted()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        byte[] buffer = new byte[16384];
        Assert.IsTrue(client.PollTransmit(Now, buffer, out _) > 0);
        TimeSpan timeout = ProbeTimeout();
        Assert.AreEqual(Now + timeout, client.NextTimeout());
        Assert.IsFalse(client.HandleTimeout(Now + timeout - TimeSpan.FromMilliseconds(1)));
        Assert.IsTrue(client.HandleTimeout(Now + timeout));
        Assert.IsTrue(client.PollTransmit(Now + timeout, buffer, out _) > 0);
    }

    [TestMethod]
    public void AReorderedPacketIsNotDeclaredLost()
    {
        var controller = new QuicNewReno();
        var loss = new QuicLossRecovery(controller);
        byte[] payload = [0x01];
        loss.OnPacketSent(QuicPacketKind.OneRtt, 0, 1200, Now, payload);
        loss.OnPacketSent(QuicPacketKind.OneRtt, 1, 1200, Now, payload);
        loss.OnPacketSent(QuicPacketKind.OneRtt, 2, 1200, Now, payload);
        var ranges = new QuicAckRange[1];
        ranges[0] = new QuicAckRange { Length = 0 };
        Assert.IsTrue(loss.OnAck(QuicPacketKind.OneRtt, 2, TimeSpan.Zero, ranges, 1, Now));
        Assert.AreEqual(0, loss.DeclaredLost);
        Assert.AreEqual(2, loss.InFlight);
        Assert.IsTrue(controller.CongestionWindow > controller.MinimumWindow);
    }

    [TestMethod]
    public void PersistentCongestionShrinksTheWindow()
    {
        var controller = new QuicNewReno();
        var loss = new QuicLossRecovery(controller);
        byte[] payload = [0x01];
        loss.OnPacketSent(QuicPacketKind.OneRtt, 0, 1200, Now, payload);
        var ranges = new QuicAckRange[1];
        ranges[0] = new QuicAckRange { Length = 0 };
        DateTimeOffset sample = Now + TimeSpan.FromMilliseconds(50);
        Assert.IsTrue(loss.OnAck(QuicPacketKind.OneRtt, 0, TimeSpan.Zero, ranges, 1, sample));

        DateTimeOffset first = sample + TimeSpan.FromMilliseconds(10);
        DateTimeOffset later = first + TimeSpan.FromSeconds(10);
        loss.OnPacketSent(QuicPacketKind.OneRtt, 1, 1200, first, payload);
        for (ulong number = 2; number <= 5; number++)
        {
            loss.OnPacketSent(QuicPacketKind.OneRtt, number, 1200, later, payload);
        }

        Assert.IsTrue(loss.OnAck(QuicPacketKind.OneRtt, 5, TimeSpan.Zero, ranges, 1, later));
        Assert.IsTrue(loss.DeclaredLost >= 2);
        Assert.AreEqual(controller.MinimumWindow, controller.CongestionWindow);
    }

    [TestMethod]
    public void AnAckOfAnUnsentPacketCloses()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        byte[] frame = new byte[32];
        var ranges = new QuicAckRange[1];
        ranges[0] = new QuicAckRange { Length = 0 };
        var ack = new QuicFrame
        {
            Kind = QuicFrameKind.Ack,
            LargestAcknowledged = 5,
            AckRangeCount = 1,
        };
        int framed = QuicFrameWriter.Write(in ack, ranges, frame);
        using var send = QuicPacketProtector.FromInitial(ServerId, client: false);
        byte[] packet = new byte[1500];
        int written = QuicPackets.SealLong(packet, QuicPacketKind.Initial, ClientId, ServerId, 0, frame.AsSpan(0, framed), send, 1200);
        Assert.IsTrue(written > 0);
        client.HandleDatagram(Now, packet.AsSpan(0, written), Path);
        Assert.IsTrue(client.IsClosed);
        Assert.AreEqual(QuicFrameReader.ProtocolViolation, client.CloseError);
    }

    private static TimeSpan ProbeTimeout()
    {
        long rtt = QuicLossRecovery.InitialRtt.Ticks;
        long variance = Math.Max(4 * (rtt / 2), QuicLossRecovery.Granularity.Ticks);
        return TimeSpan.FromTicks(rtt + variance);
    }

    private static byte[] Parameters(bool client)
    {
        var parameters = new QuicTransportParameters
        {
            InitialSourceConnectionId = client ? ClientId : ServerId,
        };
        if (!client)
        {
            parameters.OriginalDestinationConnectionId = ServerId;
        }

        byte[] buffer = new byte[256];
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
