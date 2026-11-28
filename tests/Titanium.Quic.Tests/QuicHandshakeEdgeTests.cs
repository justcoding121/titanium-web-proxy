using System;
using System.Collections.Generic;
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
        core.GrantStreamCredit(0, 100, 100);
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
    public void AClientOfferingOnlyAnUnknownProtocolIsRejected()
    {
        using X509Certificate2 certificate = Certificate();
        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        PathInfo path = QuicConnectionSession.PathOf(local, remote);
        byte[] clientId = QuicConnectionSession.NewConnectionId();
        byte[] serverId = QuicConnectionSession.NewConnectionId();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 0, server: false), applicationProtocols: ["dummy"]);
        server.StartServer(serverId, certificate: null, Limits(serverId, idleMs: 0, server: true));
        byte[] buffer = new byte[8192];
        int count = client.PollTransmit(Now, buffer, out PathInfo sent);
        server.HandleDatagram(Now, buffer.AsSpan(0, count), sent);
        Assert.IsFalse(server.CompleteHello(certificate, ["h3", "h3-29", "hq-interop", "hq-29"]));
        Assert.IsTrue(server.IsClosed);
        Assert.AreEqual(0x0100UL + 120, server.CloseError);
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
    public void AFinishedStreamSendsTheBodyAndAFinalFrame()
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
        byte[] request = "GET /hello\r\n"u8.ToArray();
        byte[] body = "hello"u8.ToArray();
        Assert.AreEqual(request.Length, client.SendStream(0, request));
        byte[] buffer = new byte[2048];
        int count = client.PollTransmit(Now, buffer, out PathInfo sent);
        Assert.IsTrue(count > 0);
        server.HandleDatagram(Now, buffer.AsSpan(0, count), sent);
        CollectionAssert.AreEqual(request, server.ReceivedStream);
        Assert.AreEqual(body.Length, server.SendStream(0, body));
        server.FinishStream(0);
        int first = server.PollTransmit(Now, buffer, out sent);
        Assert.IsTrue(first > 0);
        client.HandleDatagram(Now, buffer.AsSpan(0, first), sent);
        int second = server.PollTransmit(Now, buffer, out sent);
        Assert.IsTrue(second > 0);
        client.HandleDatagram(Now, buffer.AsSpan(0, second), sent);
        CollectionAssert.AreEqual(body, client.ReceivedStream);
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

    [TestMethod]
    public void ACryptoOffsetPastTheBufferClosesWithTheBufferError()
    {
        using X509Certificate2 certificate = Certificate();
        byte[] serverId = QuicConnectionSession.NewConnectionId();
        byte[] clientId = QuicConnectionSession.NewConnectionId();
        using var server = new QuicConnectionCore();
        server.StartServer(serverId, certificate, Limits(serverId, idleMs: 0, server: true));
        byte[] packet = InitialCrypto(serverId, clientId, (1UL << 20) + 1);
        server.HandleDatagram(Now, packet, new PathInfo([10, 0, 0, 1], 443, [10, 0, 0, 2], 9));
        Assert.IsTrue(server.IsClosed);
        Assert.AreEqual(QuicFlow.CryptoBufferExceeded, server.CloseError);
    }

    [TestMethod]
    public void AServerRejectsAClientNewTokenAndHandshakeDone()
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
        client.SendNewToken([1, 2, 3]);
        Pump(client, server);
        Assert.IsTrue(server.IsClosed);
        Assert.AreEqual(QuicFrameReader.ProtocolViolation, server.CloseError);

        using var again = new QuicConnectionCore();
        using var other = new QuicConnectionCore();
        again.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 0, server: false));
        other.StartServer(serverId, certificate, Limits(serverId, idleMs: 0, server: true));
        Pump(again, other);
        again.SendHandshakeDone();
        Pump(again, other);
        Assert.IsTrue(other.IsClosed);
        Assert.AreEqual(QuicFrameReader.ProtocolViolation, other.CloseError);

        using var third = new QuicConnectionCore();
        using var peer = new QuicConnectionCore();
        third.StartClient(path, clientId, serverId, Limits(clientId, idleMs: 0, server: false));
        peer.StartServer(serverId, certificate, Limits(serverId, idleMs: 0, server: true));
        Pump(third, peer);
        third.SendApplicationCrypto([24, 0, 0, 1, 0]);
        Pump(third, peer);
        Assert.IsTrue(peer.IsClosed);
        Assert.AreEqual(0x0100UL + 10, peer.CloseError);
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

    private static byte[] InitialCrypto(byte[] serverId, byte[] clientId, ulong offset)
    {
        byte[] one = [1];
        Span<byte> frame = stackalloc byte[32];
        var crypto = new QuicFrame { Kind = QuicFrameKind.Crypto, Offset = offset, Data = one };
        int frameLength = QuicFrameWriter.Write(in crypto, default, frame);
        Assert.IsTrue(frameLength > 0);
        const int headerLength = 26;
        byte[] plaintext = frame[..frameLength].ToArray();
        byte[] packet = new byte[headerLength + plaintext.Length + 16];
        packet[0] = 0xC0;
        packet[4] = 1;
        packet[5] = (byte)serverId.Length;
        serverId.CopyTo(packet.AsSpan(6));
        packet[14] = (byte)clientId.Length;
        clientId.CopyTo(packet.AsSpan(15));
        packet[24] = (byte)(1 + plaintext.Length + 16);
        using QuicPacketProtector protector = QuicPacketProtector.FromInitial(serverId, client: true);
        int written = protector.Seal(packet, headerLength, packetNumber: 0, plaintext);
        Assert.IsTrue(written > 0);
        return packet[..written];
    }

    [TestMethod]
    public void AnApplicationPacketValidatesTheHandshakePath()
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
        byte[] buffer = new byte[8192];
        for (int turn = 0; turn < 80 && !client.HandshakeComplete; turn++)
        {
            int count = client.PollTransmit(Now, buffer, out PathInfo sent);
            if (count > 0 && !IsHandshakePacket(buffer[0]))
            {
                server.HandleDatagram(Now, buffer.AsSpan(0, count), sent);
            }

            count = server.PollTransmit(Now, buffer, out sent);
            if (count > 0)
            {
                client.HandleDatagram(Now, buffer.AsSpan(0, count), sent);
            }
        }

        Assert.IsTrue(client.HandshakeComplete);
        Assert.IsFalse(server.IsPathValidated(path));
        Assert.AreEqual(4, client.SendStream(0, [1, 2, 3, 4]));
        int sentCount = client.PollTransmit(Now, buffer, out PathInfo oneRtt);
        Assert.IsTrue(sentCount > 0);
        server.HandleDatagram(Now, buffer.AsSpan(0, sentCount), oneRtt);
        Assert.IsFalse(server.IsClosed);
        Assert.IsTrue(server.IsPathValidated(path));
    }

    [TestMethod]
    public void AnApplicationPacketBeforeTheHandshakeIsDeliveredWhenTheHandshakeArrives()
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
        byte[] buffer = new byte[8192];
        var held = new List<byte[]>();
        for (int turn = 0; turn < 80 && !client.HandshakeComplete; turn++)
        {
            int count = client.PollTransmit(Now, buffer, out PathInfo sent);
            if (count > 0 && IsHandshakePacket(buffer[0]))
            {
                var copy = new byte[count];
                buffer.AsSpan(0, count).CopyTo(copy);
                held.Add(copy);
            }
            else if (count > 0)
            {
                server.HandleDatagram(Now, buffer.AsSpan(0, count), sent);
            }

            count = server.PollTransmit(Now, buffer, out sent);
            if (count > 0)
            {
                client.HandleDatagram(Now, buffer.AsSpan(0, count), sent);
            }
        }

        Assert.IsTrue(client.HandshakeComplete);
        Assert.IsFalse(server.HandshakeComplete);
        for (int turn = 0; turn < 10 && held.Count == 0; turn++)
        {
            int count = client.PollTransmit(Now, buffer, out PathInfo sent);
            if (count == 0)
            {
                break;
            }

            if (IsHandshakePacket(buffer[0]))
            {
                var copy = new byte[count];
                buffer.AsSpan(0, count).CopyTo(copy);
                held.Add(copy);
            }
            else
            {
                server.HandleDatagram(Now, buffer.AsSpan(0, count), sent);
            }
        }

        Assert.IsTrue(held.Count > 0);
        byte[] body = [1, 2, 3, 4];
        Assert.AreEqual(body.Length, client.SendStream(0, body));
        bool delivered = false;
        for (int turn = 0; turn < 10 && !delivered; turn++)
        {
            int count = client.PollTransmit(Now, buffer, out PathInfo sent);
            if (count == 0)
            {
                break;
            }

            if (IsHandshakePacket(buffer[0]))
            {
                var copy = new byte[count];
                buffer.AsSpan(0, count).CopyTo(copy);
                held.Add(copy);
                continue;
            }

            server.HandleDatagram(Now, buffer.AsSpan(0, count), sent);
            delivered = true;
        }

        Assert.IsTrue(delivered);
        Assert.IsFalse(server.HandshakeComplete);
        Assert.AreEqual(0, server.ReceivedStream.Length);
        foreach (byte[] packet in held)
        {
            server.HandleDatagram(Now, packet, path);
        }

        Assert.IsFalse(server.IsClosed);
        Assert.IsTrue(server.HandshakeComplete);
        CollectionAssert.AreEqual(body, server.ReceivedStream);
    }

    [TestMethod]
    public void QueuedStreamDataIsSentAfterTheCongestionWindowOpens()
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
        Assert.IsTrue(client.HandshakeComplete, "client handshake");
        Assert.IsTrue(server.HandshakeComplete, "server handshake");
        byte[] body = new byte[20000];
        body[0] = 9;
        Assert.AreEqual(body.Length, client.SendStream(0, body));
        byte[] buffer = new byte[8192];
        var flight = new List<byte[]>();
        PathInfo sentPath = path;
        for (int i = 0; i < 40; i++)
        {
            int count = client.PollTransmit(Now, buffer, out sentPath);
            if (count == 0)
            {
                break;
            }

            var copy = new byte[count];
            buffer.AsSpan(0, count).CopyTo(copy);
            flight.Add(copy);
        }

        Assert.IsTrue(flight.Count > 0);
        Assert.AreEqual(0, client.PollTransmit(Now, buffer, out _));
        foreach (byte[] packet in flight)
        {
            server.HandleDatagram(Now, packet, sentPath);
        }

        for (int i = 0; i < 10; i++)
        {
            int count = server.PollTransmit(Now, buffer, out PathInfo ackPath);
            if (count == 0)
            {
                break;
            }

            client.HandleDatagram(Now, buffer.AsSpan(0, count), ackPath);
        }

        Assert.IsTrue(client.PollTransmit(Now, buffer, out _) > 0);
    }

    private static bool IsHandshakePacket(byte first) => (first & 0x80) != 0 && ((first >> 4) & 0x03) == 2;

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
