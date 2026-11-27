using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicCampaignTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    private static readonly byte[] ClientId = [0xC1, 0xC1, 0xC1, 0xC1];

    private static readonly byte[] ServerId = [0x51, 0x51, 0x51, 0x51];

    [TestMethod]
    public void BytesArriveOnceAndInOrder()
    {
        byte[] payload = Pattern(256);
        byte[] received = Transfer(payload, static sim =>
        {
            sim.Reorder = true;
            sim.Duplicate = true;
            sim.Delay = TimeSpan.FromMilliseconds(10);
        });
        CollectionAssert.AreEqual(payload, received);
    }

    [TestMethod]
    public void OneCorruptedDatagramIsNotDeliveredAsStreamData()
    {
        byte[] payload = Pattern(128);
        byte[] received = Transfer(payload, static sim => sim.CorruptCount = 1);
        CollectionAssert.AreEqual(payload, received);
    }

    [TestMethod]
    public void AFlippedHandshakeDatagramDoesNotThrow()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        byte[] buffer = new byte[16384];
        int count = client.PollTransmit(Now, buffer, out PathInfo path);
        Assert.IsTrue(count > 16);
        byte[] mutant = buffer.AsSpan(0, count).ToArray();
        mutant[count / 2] ^= 0x01;
        server.HandleDatagram(Now, mutant, path);
        var sim = new QuicSimulator();
        DateTimeOffset time = Now;
        Assert.IsTrue(Run(client, server, sim, ref time, static (left, right) => left.HandshakeComplete && right.HandshakeComplete));
    }

    [TestMethod]
    public void ADatagramFrameClosesTheConnection()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        var sim = new QuicSimulator();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        DateTimeOffset time = Now;
        Assert.IsTrue(Run(client, server, sim, ref time, static (left, right) => left.HandshakeComplete && right.HandshakeComplete));
        byte[] payload = [9, 9, 9];
        var frame = new QuicFrame { Kind = QuicFrameKind.Datagram, Data = payload };
        byte[] encoded = new byte[16];
        int written = QuicFrameWriter.Write(frame, [], encoded);
        Assert.IsTrue(written > 1);
        server.InjectApplicationPayload(encoded.AsSpan(0, written));
        Assert.IsTrue(server.IsClosed);
        Assert.AreEqual(QuicFrameReader.ProtocolViolation, server.CloseError);
        Assert.AreEqual(0, server.ReceivedStream.Length);
    }

    [TestMethod]
    public void AnEmptyDatagramIsDropped()
    {
        var sim = new QuicSimulator();
        sim.Submit(toServer: true, ReadOnlySpan<byte>.Empty, Now);
        Assert.IsNull(sim.NextDelivery());
    }

    [TestMethod]
    public void ABlackholeDropsThePayload()
    {
        byte[] payload = Pattern(32);
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        var sim = new QuicSimulator();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        DateTimeOffset time = Now;
        Assert.IsTrue(Run(client, server, sim, ref time, static (left, right) => left.HandshakeComplete && right.HandshakeComplete));
        sim.Blackhole = true;
        Assert.AreEqual(payload.Length, client.SendStream(0, payload));
        DateTimeOffset limit = time + TimeSpan.FromSeconds(2);
        byte[] buffer = new byte[16384];
        while (time < limit && !client.IsClosed && !server.IsClosed)
        {
            Exchange(client, server, sim, buffer, time);
            time += TimeSpan.FromMilliseconds(50);
            client.HandleTimeout(time);
            server.HandleTimeout(time);
        }

        Assert.AreEqual(0, server.ReceivedStream.Length);
    }

    private static byte[] Transfer(byte[] payload, Action<QuicSimulator> arrange)
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        var sim = new QuicSimulator();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        DateTimeOffset time = Now;
        Assert.IsTrue(Run(client, server, sim, ref time, static (left, right) => left.HandshakeComplete && right.HandshakeComplete));
        arrange(sim);
        Assert.AreEqual(payload.Length, client.SendStream(0, payload));
        int need = payload.Length;
        Assert.IsTrue(Run(client, server, sim, ref time, (left, right) => right.ReceivedStream.Length >= need));
        Assert.IsFalse(client.IsClosed);
        Assert.IsFalse(server.IsClosed);
        return server.ReceivedStream[..payload.Length];
    }

    private static byte[] Pattern(int length)
    {
        var payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    private static bool Run(
        QuicConnectionCore client,
        QuicConnectionCore server,
        QuicSimulator sim,
        ref DateTimeOffset time,
        Func<QuicConnectionCore, QuicConnectionCore, bool> done)
    {
        DateTimeOffset limit = time + TimeSpan.FromSeconds(30);
        byte[] buffer = new byte[16384];
        while (time <= limit)
        {
            if (client.IsClosed || server.IsClosed)
            {
                return false;
            }

            Exchange(client, server, sim, buffer, time);
            if (done(client, server))
            {
                return true;
            }

            DateTimeOffset? jump = Soonest(Soonest(client.NextTimeout(), server.NextTimeout()), sim.NextDelivery());
            if (jump is DateTimeOffset at && at > time)
            {
                time = at;
            }
            else
            {
                time += TimeSpan.FromMilliseconds(5);
            }

            client.HandleTimeout(time);
            server.HandleTimeout(time);
        }

        return false;
    }

    private static void Exchange(QuicConnectionCore client, QuicConnectionCore server, QuicSimulator sim, byte[] buffer, DateTimeOffset time)
    {
        for (int round = 0; round < 8; round++)
        {
            while (client.PollTransmit(time, buffer, out _) is int clientCount && clientCount > 0)
            {
                sim.Submit(toServer: true, buffer.AsSpan(0, clientCount), time);
            }

            while (server.PollTransmit(time, buffer, out _) is int serverCount && serverCount > 0)
            {
                sim.Submit(toServer: false, buffer.AsSpan(0, serverCount), time);
            }

            sim.Deliver(server, toServer: true, time, Path);
            sim.Deliver(client, toServer: false, time, Path);
        }
    }

    private static DateTimeOffset? Soonest(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left < right ? left : right;
    }

    private static byte[] Parameters(bool client)
    {
        var parameters = new QuicTransportParameters
        {
            InitialSourceConnectionId = client ? ClientId : ServerId,
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
