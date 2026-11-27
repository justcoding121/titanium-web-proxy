using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicCubicComparisonTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    private static readonly byte[] ClientId = [0xC1, 0xC1, 0xC1, 0xC1];

    private static readonly byte[] ServerId = [0x51, 0x51, 0x51, 0x51];

    private const int PayloadBytes = 64 * 1024;

    [TestMethod]
    public void TheSimulatorTimesATransfer()
    {
        long renoLoop = Transfer(static () => new QuicNewReno(), TimeSpan.Zero, 0);
        long cubicLoop = Transfer(static () => new QuicCubic(), TimeSpan.Zero, 0);
        long renoDelay = Transfer(static () => new QuicNewReno(), TimeSpan.FromMilliseconds(80), 0);
        long cubicDelay = Transfer(static () => new QuicCubic(), TimeSpan.FromMilliseconds(80), 0);
        long renoLoss = Transfer(static () => new QuicNewReno(), TimeSpan.FromMilliseconds(80), 1);
        long cubicLoss = Transfer(static () => new QuicCubic(), TimeSpan.FromMilliseconds(80), 1);
        Assert.IsTrue(renoLoop > 0 && cubicLoop >= renoLoop);
        Assert.IsTrue(renoDelay > renoLoop && cubicDelay >= renoDelay);
        Assert.AreEqual(-1, renoLoss);
        Assert.AreEqual(renoLoss, cubicLoss);
    }

    private static long Transfer(Func<ICongestionController> controller, TimeSpan delay, int dropOnce)
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore(controller());
        using var server = new QuicConnectionCore(controller());
        var sim = new QuicSimulator { Delay = delay };
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        DateTimeOffset time = Now;
        if (!Run(client, server, sim, ref time, static (left, right) => left.HandshakeComplete && right.HandshakeComplete))
        {
            return -1;
        }

        var payload = new byte[PayloadBytes];
        payload[0] = 0x5A;
        payload[^1] = 0xA5;
        if (client.SendStream(0, payload) != payload.Length)
        {
            return -1;
        }

        sim.DropCount = dropOnce;
        DateTimeOffset started = time;
        bool delivered = Run(client, server, sim, ref time, static (left, right) => right.ReceivedStream.Length >= PayloadBytes);
        if (!delivered || client.IsClosed || server.IsClosed)
        {
            return -1;
        }

        byte[] received = server.ReceivedStream;
        if (received.Length < PayloadBytes || received[0] != 0x5A || received[PayloadBytes - 1] != 0xA5)
        {
            return -1;
        }

        return (time - started).Ticks;
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
