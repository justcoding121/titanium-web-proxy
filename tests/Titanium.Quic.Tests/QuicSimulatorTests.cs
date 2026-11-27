using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicSimulatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    private static readonly byte[] ClientId = [0xC1, 0xC1, 0xC1, 0xC1];

    private static readonly byte[] ServerId = [0x51, 0x51, 0x51, 0x51];

    [TestMethod]
    public void ADroppedDatagramIsRetransmittedAndTheHandshakeCompletes()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        var sim = new QuicSimulator { DropCount = 1 };
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, idle: false));
        server.StartServer(ServerId, certificate, Parameters(client: false, idle: false));
        byte[] buffer = new byte[16384];
        int first = client.PollTransmit(Now, buffer, out _);
        Assert.IsTrue(first > 0);
        sim.Submit(toServer: true, buffer.AsSpan(0, first), Now);
        DateTimeOffset? due = client.NextTimeout();
        Assert.IsTrue(due.HasValue);
        DateTimeOffset retryAt = due.Value;
        Assert.IsTrue(client.HandleTimeout(retryAt));
        int again = client.PollTransmit(retryAt, buffer, out _);
        Assert.IsTrue(again > 0);
        sim.Submit(toServer: true, buffer.AsSpan(0, again), retryAt);
        Assert.IsTrue(Pump(client, server, sim, retryAt, 24));
    }

    [TestMethod]
    public void ReorderedDatagramsStillCompleteTheHandshake()
    {
        var sim = new QuicSimulator();
        using var pair = Open(sim);
        Assert.IsTrue(Pump(pair.Client, pair.Server, sim, Now, 24));
        sim.Reorder = true;
        byte[] buffer = new byte[16384];
        pair.Client.SendKeepAlive();
        int first = pair.Client.PollTransmit(Now, buffer, out _);
        Assert.IsTrue(first > 0);
        sim.Submit(toServer: true, buffer.AsSpan(0, first), Now);
        pair.Client.SendKeepAlive();
        int second = pair.Client.PollTransmit(Now, buffer, out _);
        Assert.IsTrue(second > 0);
        sim.Submit(toServer: true, buffer.AsSpan(0, second), Now);
        sim.Deliver(pair.Server, toServer: true, Now, Path);
        Assert.IsFalse(pair.Client.IsClosed);
        Assert.IsFalse(pair.Server.IsClosed);
    }

    [TestMethod]
    public void ADuplicatedDatagramDoesNotCloseTheConnection()
    {
        using var pair = Open(new QuicSimulator { Duplicate = true });
        Assert.IsTrue(Pump(pair.Client, pair.Server, pair.Sim, Now, 24));
        Assert.IsFalse(pair.Client.IsClosed);
    }

    [TestMethod]
    public void ADelayedDatagramIsDeliveredWhenTheClockAdvances()
    {
        var sim = new QuicSimulator { Delay = TimeSpan.FromMilliseconds(200) };
        using var pair = Open(sim);
        DateTimeOffset time = Now;
        bool done = false;
        for (int i = 0; i < 16 && !done; i++)
        {
            done = Pump(pair.Client, pair.Server, sim, time, 1);
            time += sim.Delay;
        }

        Assert.IsTrue(done);
    }

    [TestMethod]
    public void AnIdleConnectionTimesOutWhenTheSimulatorClockAdvances()
    {
        using var pair = Open(new QuicSimulator(), idle: true);
        Assert.IsTrue(Pump(pair.Client, pair.Server, pair.Sim, Now, 24));
        Assert.IsFalse(pair.Client.IdleTimedOut);
        Assert.IsTrue(pair.Client.HandleTimeout(Now + TimeSpan.FromMilliseconds(5000)));
        Assert.IsTrue(pair.Client.IdleTimedOut);
    }

    [TestMethod]
    public void EveryEighthDatagramIsDropped()
    {
        var sim = new QuicSimulator { LossEvery = 8 };
        var qlog = new QuicQlog { Enabled = true };
        sim.Qlog = qlog;
        byte[] datagram = new byte[1200];
        for (int i = 0; i < 8; i++)
        {
            sim.Submit(toServer: true, datagram, Now);
        }

        using var server = new QuicConnectionCore();
        sim.Deliver(server, toServer: true, Now, Path);
        Assert.AreEqual(1, qlog.Events.FindAll(static name => name == "packet_dropped").Count);
        Assert.AreEqual(7, qlog.Events.FindAll(static name => name == "packet_received").Count);
    }

    [TestMethod]
    public void AnMtuOf1400DropsTheOversizedDatagram()
    {
        var sim = new QuicSimulator { Mtu = 1400 };
        var qlog = new QuicQlog { Enabled = true };
        sim.Qlog = qlog;
        sim.Submit(toServer: true, new byte[1500], Now);
        sim.Submit(toServer: true, new byte[1200], Now);
        using var server = new QuicConnectionCore();
        sim.Deliver(server, toServer: true, Now, Path);
        Assert.IsTrue(qlog.Events.Contains("packet_dropped"));
        Assert.IsTrue(qlog.Events.Contains("packet_received"));
        var silent = new QuicQlog();
        silent.Write("packet_received");
        Assert.AreEqual(0, silent.Events.Count);
    }

    [TestMethod]
    public void ExactSizeDirectionDelayAndLossAreEdges()
    {
        var sized = new QuicSimulator { Mtu = 1200, Qlog = new QuicQlog { Enabled = true } };
        sized.Submit(toServer: true, ReadOnlySpan<byte>.Empty, Now);
        sized.Submit(toServer: true, new byte[1200], Now);
        sized.Submit(toServer: true, new byte[1201], Now);
        Assert.AreEqual(Now, sized.NextDelivery());
        using var server = new QuicConnectionCore();
        sized.Deliver(server, toServer: false, Now, Path);
        Assert.AreEqual(0, Count(sized.Qlog, "packet_received"));
        sized.Deliver(server, toServer: true, Now, Path);
        Assert.AreEqual(1, Count(sized.Qlog, "packet_received"));
        Assert.AreEqual(2, Count(sized.Qlog, "packet_dropped"));

        var later = new QuicSimulator { Delay = TimeSpan.FromMilliseconds(5), Qlog = new QuicQlog { Enabled = true } };
        later.Submit(toServer: true, new byte[100], Now + TimeSpan.FromMilliseconds(50));
        later.Submit(toServer: true, new byte[100], Now);
        Assert.AreEqual(Now + later.Delay, later.NextDelivery());
        later.Deliver(server, toServer: true, Now, Path);
        Assert.AreEqual(0, Count(later.Qlog, "packet_received"));
        later.Deliver(server, toServer: true, Now + later.Delay, Path);
        Assert.AreEqual(1, Count(later.Qlog, "packet_received"));

        var duplicated = new QuicSimulator { Duplicate = true, Qlog = new QuicQlog { Enabled = true } };
        duplicated.Submit(toServer: true, new byte[50], Now);
        duplicated.Deliver(server, toServer: true, Now, Path);
        Assert.AreEqual(2, Count(duplicated.Qlog, "packet_received"));

        var hole = new QuicSimulator { Blackhole = true, Qlog = new QuicQlog { Enabled = true } };
        hole.Submit(toServer: true, new byte[50], Now);
        Assert.AreEqual(1, Count(hole.Qlog, "packet_dropped"));
        Assert.IsNull(hole.NextDelivery());

        var every = new QuicSimulator { LossEvery = 1, Qlog = new QuicQlog { Enabled = true } };
        every.Submit(toServer: true, new byte[50], Now);
        Assert.AreEqual(1, Count(every.Qlog, "packet_dropped"));
        Assert.IsNull(every.NextDelivery());

        var corrupt = new QuicSimulator { CorruptCount = 1, Qlog = new QuicQlog { Enabled = true } };
        corrupt.Submit(toServer: true, new byte[50], Now);
        Assert.AreEqual(0, corrupt.Qlog!.Events.Count);
        Assert.AreEqual(Now, corrupt.NextDelivery());

        var reorder = new QuicSimulator { Reorder = true };
        reorder.Submit(toServer: true, new byte[40], Now);
        reorder.Submit(toServer: false, new byte[40], Now);
        reorder.Submit(toServer: true, new byte[40], Now);
        Assert.AreEqual(Now, reorder.NextDelivery());
    }

    private static int Count(QuicQlog? qlog, string name)
    {
        return qlog is null ? 0 : qlog.Events.FindAll(item => item == name).Count;
    }

    private static bool Pump(QuicConnectionCore client, QuicConnectionCore server, QuicSimulator sim, DateTimeOffset time, int steps)
    {
        byte[] buffer = new byte[16384];
        for (int step = 0; step < steps; step++)
        {
            while (client.PollTransmit(time, buffer, out _) is int count && count > 0)
            {
                sim.Submit(toServer: true, buffer.AsSpan(0, count), time);
            }

            while (server.PollTransmit(time, buffer, out _) is int count && count > 0)
            {
                sim.Submit(toServer: false, buffer.AsSpan(0, count), time);
            }

            sim.Deliver(server, toServer: true, time, Path);
            sim.Deliver(client, toServer: false, time, Path);
            if (client.HandshakeComplete && server.HandshakeComplete)
            {
                return true;
            }
        }

        return client.HandshakeComplete && server.HandshakeComplete;
    }

    private static Pair Open(QuicSimulator sim, bool idle = false)
    {
        var certificate = Certificate();
        var client = new QuicConnectionCore();
        var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true, idle));
        server.StartServer(ServerId, certificate, Parameters(client: false, idle));
        return new Pair(client, server, sim, certificate);
    }

    private static byte[] Parameters(bool client, bool idle)
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

        if (idle)
        {
            parameters.MaxIdleTimeout = 5000;
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

    private sealed class Pair : IDisposable
    {
        internal Pair(QuicConnectionCore client, QuicConnectionCore server, QuicSimulator sim, X509Certificate2 certificate)
        {
            Client = client;
            Server = server;
            Sim = sim;
            _certificate = certificate;
        }

        internal QuicConnectionCore Client { get; }

        internal QuicConnectionCore Server { get; }

        internal QuicSimulator Sim { get; }

        private readonly X509Certificate2 _certificate;

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
            _certificate.Dispose();
        }
    }
}
