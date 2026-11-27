using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicHandshakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    private static readonly byte[] ClientId = [0xC1, 0xC1, 0xC1, 0xC1];

    private static readonly byte[] ServerId = [0x51, 0x51, 0x51, 0x51];

    [TestMethod]
    public void TwoCoresReachApplicationDataAndExchangeOneByte()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, match: true));
        server.StartServer(ServerId, certificate, Parameters(client: false, match: true));
        Pump(client, server);
        Assert.IsTrue(client.HandshakeComplete);
        Assert.IsTrue(server.HandshakeComplete);
        Assert.IsFalse(client.IsClosed);
        Assert.IsFalse(server.IsClosed);

        client.SendStreamByte(0x5A);
        Pump(client, server);
        Assert.AreEqual((byte)0x5A, server.ReceivedStreamByte);
    }

    [TestMethod]
    public void ABadFinishedClosesTheConnection()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, match: true));
        server.StartServer(ServerId, certificate, Parameters(client: false, match: true));
        client.CorruptNextHandshakePayload = true;
        Pump(client, server);
        Assert.IsTrue(server.IsClosed);
        Assert.AreEqual(0x0100UL + 51, server.CloseError);
    }

    [TestMethod]
    public void ATransportParameterMismatchClosesTheConnection()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, match: true));
        server.StartServer(ServerId, certificate, Parameters(client: false, match: false));
        Pump(client, server);
        Assert.IsTrue(client.IsClosed);
        Assert.AreEqual(QuicTransportParametersCodec.TransportParameterError, client.CloseError);
    }

    [TestMethod]
    public void HandshakeBytesAboveTheTlsCapAreACryptoError()
    {
        using X509Certificate2 certificate = Certificate();
        using var server = new QuicConnectionCore();
        server.StartServer(ServerId, certificate, Parameters(client: false, match: true));
        byte[] message = [0x01, 0x03, 0x0D, 0x40];
        Span<byte> frame = stackalloc byte[16];
        var crypto = new QuicFrame { Kind = QuicFrameKind.Crypto, Data = message };
        int framed = QuicFrameWriter.Write(in crypto, default, frame);
        using var send = QuicPacketProtector.FromInitial(ServerId, client: true);
        byte[] packet = new byte[1500];
        int written = QuicPackets.SealLong(packet, QuicPacketKind.Initial, ServerId, ClientId, 0, frame[..framed], send, 1200);
        Assert.IsTrue(written > 0);
        server.HandleDatagram(Now, packet.AsSpan(0, written), Path);
        Assert.IsTrue(server.IsClosed);
        Assert.AreEqual(0x0100UL + 50, server.CloseError);
    }

    private static void Pump(QuicConnectionCore left, QuicConnectionCore right)
    {
        byte[] buffer = new byte[16384];
        for (int i = 0; i < 12; i++)
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

    private static byte[] Parameters(bool client, bool match)
    {
        var parameters = new QuicTransportParameters
        {
            InitialSourceConnectionId = client ? ClientId : ServerId,
            InitialMaxData = 1 << 20,
            InitialMaxStreamDataBidiLocal = 1 << 20,
            InitialMaxStreamDataBidiRemote = 1 << 20,
            InitialMaxStreamDataUni = 1 << 20,
            InitialMaxStreamsBidi = 16,
            InitialMaxStreamsUni = 16,
        };
        if (!client)
        {
            parameters.OriginalDestinationConnectionId = match ? ServerId : [0xFF];
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
