using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    private static readonly PathInfo Other = new([10, 0, 0, 1], 443, [10, 0, 0, 3], 50001);

    private static readonly byte[] ClientId = [0xC1, 0xC1, 0xC1, 0xC1];

    private static readonly byte[] ServerId = [0x51, 0x51, 0x51, 0x51];

    private static readonly byte[] RetryToken = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];

    private static readonly byte[] ResetToken =
    [
        0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5,
        0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A,
    ];

    [TestMethod]
    public void ARetryRoundTripUsesTheNewDestinationConnectionId()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, reset: false));
        server.StartServer(ServerId, certificate, Parameters(client: false, reset: false));
        server.ArmRetry(RetryToken);
        byte[] buffer = new byte[16384];
        int first = client.PollTransmit(Now, buffer, out PathInfo path);
        Assert.IsTrue(first > 0);
        server.HandleDatagram(Now, buffer.AsSpan(0, first), path);
        int retry = server.PollTransmit(Now, buffer, out path);
        Assert.IsTrue(retry > 0);
        Assert.AreEqual(0xF0, buffer[0]);
        client.HandleDatagram(Now, buffer.AsSpan(0, retry), path);
        int second = client.PollTransmit(Now, buffer, out path);
        Assert.IsTrue(second > 0);
        var reader = new QuicDatagramReader(buffer.AsSpan(0, second), 0);
        Assert.IsTrue(reader.TryRead(out QuicPacket packet));
        Assert.AreEqual(QuicPacketKind.Initial, packet.Kind);
        Assert.AreEqual(8, packet.DestinationConnectionId.Length);
        CollectionAssert.AreEqual(RetryToken, packet.Token.ToArray());
        server.HandleDatagram(Now, buffer.AsSpan(0, second), path);
        Assert.IsNotNull(server.ReceivedInitialToken);
        CollectionAssert.AreEqual(RetryToken, server.ReceivedInitialToken);
    }

    [TestMethod]
    public void ARetryWithABadTagIsIgnored()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, reset: false));
        server.StartServer(ServerId, certificate, Parameters(client: false, reset: false));
        server.ArmRetry(RetryToken);
        byte[] buffer = new byte[16384];
        int first = client.PollTransmit(Now, buffer, out PathInfo path);
        server.HandleDatagram(Now, buffer.AsSpan(0, first), path);
        int retry = server.PollTransmit(Now, buffer, out path);
        buffer[retry - 1] ^= 0xFF;
        client.HandleDatagram(Now, buffer.AsSpan(0, retry), path);
        Assert.AreEqual(0, client.PollTransmit(Now, buffer, out _));
        Assert.IsFalse(client.IsClosed);
    }

    [TestMethod]
    public void ANewTokenIsUsedOnTheNextConnection()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, reset: false));
        server.StartServer(ServerId, certificate, Parameters(client: false, reset: false));
        Pump(client, server);
        byte[] token = [0x71, 0x72, 0x73, 0x74];
        server.SendNewToken(token);
        Pump(client, server);
        Assert.IsNotNull(client.IssuedNewToken);
        CollectionAssert.AreEqual(token, client.IssuedNewToken);

        using var next = new QuicConnectionCore();
        next.StartClient(Path, ClientId, ServerId, Parameters(client: true, reset: false), token);
        byte[] buffer = new byte[16384];
        int count = next.PollTransmit(Now, buffer, out _);
        Assert.IsTrue(count > 0);
        var reader = new QuicDatagramReader(buffer.AsSpan(0, count), 0);
        Assert.IsTrue(reader.TryRead(out QuicPacket packet));
        CollectionAssert.AreEqual(token, packet.Token.ToArray());
    }

    [TestMethod]
    public void AStatelessResetClosesWithoutDecrypting()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, reset: true));
        server.StartServer(ServerId, certificate, Parameters(client: false, reset: true));
        Pump(client, server);
        byte[] bogus = new byte[48];
        bogus[0] = 0x40;
        ResetToken.CopyTo(bogus.AsSpan(^16));
        client.HandleDatagram(Now, bogus, Path);
        Assert.IsTrue(client.ClosedByStatelessReset);
        byte[] buffer = new byte[256];
        Assert.AreEqual(0, client.PollTransmit(Now, buffer, out _));
    }

    [TestMethod]
    public void APathChallengeValidatesTheNewPath()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, reset: false));
        server.StartServer(ServerId, certificate, Parameters(client: false, reset: false));
        Pump(client, server);
        Assert.IsFalse(client.IsPathValidated(Other));
        byte[] sink = new byte[16384];
        while (client.PollTransmit(Now, sink, out _) > 0)
        {
        }

        while (server.PollTransmit(Now, sink, out _) > 0)
        {
        }

        client.SendPathChallenge(Other);
        byte[] buffer = new byte[2048];
        int challenge = client.PollTransmit(Now, buffer, out PathInfo path);
        Assert.IsTrue(challenge >= 1200);
        Assert.IsTrue(path.Equals(Other));
        server.HandleDatagram(Now, buffer.AsSpan(0, challenge), path);
        int response = server.PollTransmit(Now, buffer, out path);
        Assert.IsTrue(response > 0);
        client.HandleDatagram(Now, buffer.AsSpan(0, response), path);
        Assert.IsTrue(client.IsPathValidated(Other));
    }

    [TestMethod]
    public void ANewConnectionIdIsAccepted()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, reset: false));
        server.StartServer(ServerId, certificate, Parameters(client: false, reset: false));
        Pump(client, server);
        Assert.AreEqual(1, client.PeerConnectionIds);
        server.IssueConnectionId();
        Pump(client, server);
        Assert.AreEqual(2, client.PeerConnectionIds);
        Assert.IsFalse(client.IsClosed);
    }

    [TestMethod]
    public void AResetTokenForTheOriginalDestinationIsNotTheSendTarget()
    {
        var ids = new QuicIdentity();
        byte[] server = [0x13, 0x92, 0xE0, 0x5D, 0x63, 0xCA, 0xAF, 0xD8, 0x27];
        byte[] original = [0xD2, 0x45, 0x1C, 0x73, 0x83, 0x1C, 0x94, 0xB9];
        ids.AddPeer(server);
        ids.RememberResetToken(original, ResetToken);
        CollectionAssert.AreEqual(server, ids.ActivePeer());
        Assert.AreEqual(1, ids.PeerActive);
        ids.MarkUsed(original);
        byte[] datagram = new byte[40];
        ResetToken.CopyTo(datagram.AsSpan(datagram.Length - 16));
        Assert.IsTrue(ids.MatchesReset(datagram));
    }

    [TestMethod]
    public void RetiringTheLastConnectionIdClosesTheConnection()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, reset: false));
        server.StartServer(ServerId, certificate, Parameters(client: false, reset: false));
        Pump(client, server);
        client.RetireConnectionId(0);
        Pump(client, server);
        Assert.IsTrue(server.IsClosed);
        Assert.AreEqual(QuicFrameReader.ProtocolViolation, server.CloseError);
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

    private static byte[] Parameters(bool client, bool reset)
    {
        var parameters = new QuicTransportParameters
        {
            InitialSourceConnectionId = client ? ClientId : ServerId,
        };
        if (!client)
        {
            parameters.OriginalDestinationConnectionId = ServerId;
            if (reset)
            {
                parameters.StatelessResetToken = (byte[])ResetToken.Clone();
            }
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
