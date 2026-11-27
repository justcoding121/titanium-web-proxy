using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicHandshakeEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CallsBeforeTheKeysExistDoNotSend()
    {
        using X509Certificate2 certificate = Certificate();
        using var core = new QuicConnectionCore();
        var path = new PathInfo([10, 0, 0, 1], 443, [10, 0, 0, 2], 9);
        Assert.AreEqual(string.Empty, core.PendingServerName);
        Assert.IsFalse(core.PendingHello);
        Assert.IsFalse(core.CompleteHello(certificate));
        Assert.AreEqual(0, core.SendStream(0, [1, 2, 3]));
        core.SendStreamByte(1);
        core.SendNewToken([]);
        core.SendNewToken([9]);
        core.IssueConnectionId();
        core.RetireConnectionId(1);
        core.SendKeepAlive();
        core.SendPathChallenge(path);
        core.GrantStreamCredit(0, 100);
        Assert.IsFalse(core.SendPathMtuProbe());
        Assert.IsFalse(core.SendPathMtuProbe(confirm: true));
        core.ReportTunnelMaximum(100);
        Assert.AreEqual(QuicPathMtu.Base, core.PathMtu);
        Assert.IsFalse(core.IsClosed);

        core.AcknowledgeApplication(0, Now);
        Assert.IsTrue(core.IsClosed);
        Assert.AreEqual(QuicFrameReader.ProtocolViolation, core.CloseError);
        core.Close(7);
        Assert.AreEqual(QuicFrameReader.ProtocolViolation, core.CloseError);
        Assert.AreEqual(0, core.PollTransmit(Now, new byte[2048], out _));
        core.Dispose();
    }

    [TestMethod]
    public void AClientCloseStagesOneDatagram()
    {
        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        using var core = new QuicConnectionCore(new QuicCubic());
        byte[] id = QuicConnectionSession.NewConnectionId();
        core.StartClient(QuicConnectionSession.PathOf(local, remote), id, id, QuicConnectionSession.Parameters(client: true, id), serverName: "example.test");
        Assert.AreEqual(string.Empty, core.PendingServerName);
        core.Close(0x1c);
        Assert.AreEqual(0x1cUL, core.CloseError);
        Assert.IsTrue(core.PollTransmit(Now, new byte[8192], out _) > 0);
        core.Dispose();
    }

    [TestMethod]
    public void TheFirstClientDatagramIsPaddedAndTheServerSeesTheName()
    {
        using X509Certificate2 certificate = Certificate();
        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        PathInfo path = QuicConnectionSession.PathOf(local, remote);
        byte[] clientId = QuicConnectionSession.NewConnectionId();
        byte[] serverId = QuicConnectionSession.NewConnectionId();
        using var named = new QuicConnectionCore();
        using var seen = new QuicConnectionCore();
        named.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 0, server: false), serverName: "example.test");
        byte[] buffer = new byte[8192];
        Assert.AreEqual(4096, named.PollTransmit(Now, buffer, out PathInfo sent));
        seen.StartServer(serverId, certificate, Limits(serverId, idleMs: 60_000, server: true));
        seen.HandleDatagram(Now, buffer.AsSpan(0, 4096), sent);
        Assert.AreEqual("example.test", seen.PendingServerName);

        using var plain = new QuicConnectionCore();
        plain.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 0, server: false));
        Assert.AreEqual(4096, plain.PollTransmit(Now, buffer, out _));
        using var blank = new QuicConnectionCore();
        blank.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 0, server: false), serverName: "");
        Assert.AreEqual(4096, blank.PollTransmit(Now, buffer, out _));
    }

    [TestMethod]
    public void ADeferredHelloRejectsAProtocolTheClientDidNotOffer()
    {
        using X509Certificate2 certificate = Certificate();
        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        PathInfo path = QuicConnectionSession.PathOf(local, remote);
        byte[] clientId = QuicConnectionSession.NewConnectionId();
        byte[] serverId = QuicConnectionSession.NewConnectionId();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 0, server: false), serverName: "example.test", applicationProtocols: ["h3"]);
        server.StartServer(serverId, certificate: null, Limits(serverId, idleMs: 0, server: true));
        byte[] buffer = new byte[8192];
        int count = client.PollTransmit(Now, buffer, out PathInfo sent);
        server.HandleDatagram(Now, buffer.AsSpan(0, count), sent);
        Assert.IsTrue(server.PendingHello);
        Assert.AreEqual("example.test", server.PendingServerName);
        Assert.IsFalse(server.CompleteHello(certificate, ["nope"]));
        Assert.IsTrue(server.IsClosed);

        using var again = new QuicConnectionCore();
        using var resumed = new QuicConnectionCore();
        again.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 0, server: false), applicationProtocols: ["h3"]);
        resumed.StartServer(serverId, certificate: null, Limits(serverId, idleMs: 0, server: true));
        count = again.PollTransmit(Now, buffer, out sent);
        resumed.HandleDatagram(Now, buffer.AsSpan(0, count), sent);
        Assert.IsTrue(resumed.CompleteHello(certificate, ["h3"]));
        Assert.IsFalse(resumed.IsClosed);
        Assert.IsFalse(resumed.CompleteHello(certificate, []));
    }

    [TestMethod]
    public void ATinyPeerIdleIsFlooredAndAShortRetryIsIgnored()
    {
        using X509Certificate2 certificate = Certificate();
        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        PathInfo path = QuicConnectionSession.PathOf(local, remote);
        byte[] clientId = QuicConnectionSession.NewConnectionId();
        byte[] serverId = QuicConnectionSession.NewConnectionId();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 60_000, server: false), serverName: "localhost");
        server.StartServer(serverId, certificate, Limits(serverId, idleMs: 1, server: true));
        Pump(client, server);
        byte[] sink = new byte[8192];
        while (client.PollTransmit(Now, sink, out _) > 0)
        {
        }

        Assert.IsFalse(client.HandleTimeout(Now + TimeSpan.FromMilliseconds(2)) && client.IdleTimedOut);
        Assert.IsFalse(client.IdleTimedOut);
        Assert.IsTrue(client.HandleTimeout(Now + TimeSpan.FromMilliseconds(200)));
        Assert.IsTrue(client.IdleTimedOut);

        using var fresh = new QuicConnectionCore();
        fresh.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 0, server: false));
        fresh.PollTransmit(Now, sink, out _);
        fresh.HandleDatagram(Now, new byte[] { 0xF0, 0, 0, 0, 1 }, path);
        Assert.IsFalse(fresh.IsClosed);
        Assert.AreEqual(0, fresh.PollTransmit(Now, sink, out _));
    }

    [TestMethod]
    public void AStreamLargerThanTheWindowIsDelivered()
    {
        using X509Certificate2 certificate = Certificate();
        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        PathInfo path = QuicConnectionSession.PathOf(local, remote);
        byte[] clientId = QuicConnectionSession.NewConnectionId();
        byte[] serverId = QuicConnectionSession.NewConnectionId();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 0, server: false));
        server.StartServer(serverId, certificate, Limits(serverId, idleMs: 0, server: true));
        Pump(client, server);
        byte[] payload = new byte[20_000];
        payload[0] = 0x11;
        payload[^1] = 0x22;
        Assert.AreEqual(payload.Length, client.SendStream(0, payload));
        Pump(client, server);
        CollectionAssert.AreEqual(payload, server.ReceivedStream);
    }

    [TestMethod]
    public void ReservedHeaderBitsCloseTheConnection()
    {
        Assert.IsFalse(QuicPacket.ReservedBitsSet(0xC3));
        Assert.IsTrue(QuicPacket.ReservedBitsSet(0xCC));
        Assert.IsFalse(QuicPacket.ReservedBitsSet(0x40));
        Assert.IsTrue(QuicPacket.ReservedBitsSet(0x58));

        using X509Certificate2 certificate = Certificate();
        byte[] serverId = QuicConnectionSession.NewConnectionId();
        byte[] clientId = QuicConnectionSession.NewConnectionId();
        using var server = new QuicConnectionCore();
        server.StartServer(serverId, certificate, Limits(serverId, idleMs: 0, server: true));
        byte[] packet = InitialWithReservedBits(serverId, clientId);
        var path = new PathInfo([10, 0, 0, 1], 443, [10, 0, 0, 2], 9);
        server.HandleDatagram(Now, packet, path);
        Assert.IsTrue(server.IsClosed);
        Assert.AreEqual(QuicFrameReader.ProtocolViolation, server.CloseError);
    }

    private static byte[] InitialWithReservedBits(byte[] serverId, byte[] clientId)
    {
        const int headerLength = 26;
        byte[] plaintext = new byte[32];
        byte[] packet = new byte[headerLength + plaintext.Length + 16];
        packet[0] = 0xCC;
        packet[1] = 0;
        packet[2] = 0;
        packet[3] = 0;
        packet[4] = 1;
        packet[5] = (byte)serverId.Length;
        serverId.CopyTo(packet.AsSpan(6));
        packet[14] = (byte)clientId.Length;
        clientId.CopyTo(packet.AsSpan(15));
        packet[23] = 0;
        packet[24] = (byte)(1 + plaintext.Length + 16);
        using QuicPacketProtector protector = QuicPacketProtector.FromInitial(serverId, client: true);
        int written = protector.Seal(packet, headerLength, packetNumber: 0, plaintext);
        Assert.IsTrue(written > 0);
        return packet[..written];
    }

    private static void Pump(QuicConnectionCore left, QuicConnectionCore right)
    {
        byte[] buffer = new byte[16384];
        for (int i = 0; i < 80; i++)
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

    private static byte[] Limits(byte[] sourceId, ulong idleMs, bool server)
    {
        var parameters = new QuicTransportParameters
        {
            InitialSourceConnectionId = sourceId,
            OriginalDestinationConnectionId = server ? sourceId : null,
            MaxIdleTimeout = idleMs,
            InitialMaxData = 1 << 20,
            InitialMaxStreamDataBidiLocal = 1 << 20,
            InitialMaxStreamDataBidiRemote = 1 << 20,
            InitialMaxStreamDataUni = 1 << 20,
            InitialMaxStreamsBidi = 16,
            InitialMaxStreamsUni = 16,
            MaxUdpPayloadSize = 1452,
        };
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
