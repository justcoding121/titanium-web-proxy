using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicIdleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    private static readonly PathInfo Other = new([10, 0, 0, 1], 443, [10, 0, 0, 3], 50001);

    private static readonly byte[] ClientId = [0xC1, 0xC1, 0xC1, 0xC1];

    private static readonly byte[] ServerId = [0x51, 0x51, 0x51, 0x51];

    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(5000);

    [TestMethod]
    public void AnIdleConnectionTimesOut()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        Pump(client, server);
        Assert.IsFalse(client.HandleTimeout(Now + Idle - TimeSpan.FromMilliseconds(1)) && client.IdleTimedOut);
        Assert.IsFalse(client.IdleTimedOut);
        Assert.IsTrue(client.HandleTimeout(Now + Idle));
        Assert.IsTrue(client.IdleTimedOut);
        byte[] buffer = new byte[256];
        Assert.AreEqual(0, client.PollTransmit(Now + Idle, buffer, out _));
    }

    [TestMethod]
    public void AKeepAlivePreventsTheIdleTimeout()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        Pump(client, server);
        byte[] buffer = new byte[16384];
        while (client.PollTransmit(Now, buffer, out _) > 0)
        {
        }

        DateTimeOffset sent = Now + TimeSpan.FromSeconds(2);
        client.SendKeepAlive();
        int count = client.PollTransmit(sent, buffer, out PathInfo path);
        Assert.IsTrue(count > 0);
        server.HandleDatagram(sent, buffer.AsSpan(0, count), path);
        Assert.IsFalse(server.IdleTimedOut);
        server.HandleTimeout(Now + Idle);
        Assert.IsFalse(server.IdleTimedOut);
        Assert.IsTrue(server.HandleTimeout(sent + Idle));
        Assert.IsTrue(server.IdleTimedOut);
    }

    [TestMethod]
    public void AnUnvalidatedPathDoesNotRaiseTheSendLimit()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        Pump(client, server);
        byte[] buffer = new byte[16384];
        while (client.PollTransmit(Now, buffer, out _) > 0)
        {
        }

        client.Migrate(Other);
        Assert.IsFalse(client.IsPathValidated(Other));
        client.SendKeepAlive();
        int sent = client.PollTransmit(Now, buffer, out PathInfo path);
        Assert.IsTrue(sent > 0);
        Assert.IsTrue(path.Equals(Other));
        long window = client.CongestionWindow;
        long inFlight = client.BytesInFlight;
        Span<byte> frame = stackalloc byte[16];
        var ranges = new QuicAckRange[1];
        var ack = new QuicFrame
        {
            Kind = QuicFrameKind.Ack,
            LargestAcknowledged = 1,
            AckRangeCount = 1,
        };
        int written = QuicFrameWriter.Write(in ack, ranges, frame);
        client.InjectApplicationPayload(frame[..written]);
        Assert.IsFalse(client.IsClosed);
        Assert.AreEqual(window, client.CongestionWindow);
        Assert.IsTrue(client.BytesInFlight < inFlight);
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

    private static byte[] Parameters(bool client)
    {
        var parameters = new QuicTransportParameters
        {
            InitialSourceConnectionId = client ? ClientId : ServerId,
            MaxIdleTimeout = (ulong)Idle.TotalMilliseconds,
            InitialMaxData = 1 << 20,
            InitialMaxStreamDataBidiLocal = 1 << 20,
            InitialMaxStreamDataBidiRemote = 1 << 20,
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
