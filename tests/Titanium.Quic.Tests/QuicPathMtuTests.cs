using System;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicPathMtuTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly PathInfo Path = new([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);

    private static readonly byte[] ClientId = [0xC1, 0xC1, 0xC1, 0xC1];

    private static readonly byte[] ServerId = [0x51, 0x51, 0x51, 0x51];

    [TestMethod]
    public void APathOf1400SettlesBetween1200And1400()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        client.StartClient(Path, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        Pump(client, server);
        Assert.IsTrue(client.HandshakeComplete);
        Assert.AreEqual(QuicPathMtu.Base, client.PathMtu);
        Search(client, 1400);
        Assert.IsTrue(client.PathMtu > QuicPathMtu.Base);
        Assert.IsTrue(client.PathMtu <= 1400);
        client.ReportTunnelMaximum(1300);
        Assert.IsTrue(client.PathMtu <= 1300);
        Assert.IsTrue(client.PathMtu >= QuicPathMtu.Base);
        LoseConfirmation(client);
        Assert.AreEqual(QuicPathMtu.Base, client.PathMtu);
    }

    [TestMethod]
    public void DontFragmentIsSetWhereTheSocketSupportsIt()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        bool before = socket.DontFragment;
        bool enabled = QuicDatagramSocket.TryEnableDontFragment(socket);
        if (enabled)
        {
            Assert.IsTrue(socket.DontFragment);
        }
        else
        {
            Assert.AreEqual(before, socket.DontFragment);
        }
    }

    private static void Search(QuicConnectionCore client, int mtu)
    {
        byte[] buffer = new byte[65536];
        DateTimeOffset time = Now;
        for (int step = 0; step < 40; step++)
        {
            Drain(client, time, buffer);
            if (!client.SendPathMtuProbe())
            {
                return;
            }

            int size = client.PollTransmit(time, buffer, out _);
            Assert.IsTrue(size > 0);
            ulong probe = client.LastSentApplication;
            if (size <= mtu)
            {
                client.AcknowledgeApplication(probe, time);
                continue;
            }

            ulong newest = 0;
            for (int extra = 0; extra < 3; extra++)
            {
                client.SendKeepAlive();
                Assert.IsTrue(client.PollTransmit(time, buffer, out _) > 0);
                newest = client.LastSentApplication;
            }

            client.AcknowledgeApplication(newest, time + TimeSpan.FromMilliseconds(1));
        }
    }

    private static void LoseConfirmation(QuicConnectionCore client)
    {
        byte[] buffer = new byte[65536];
        DateTimeOffset time = Now + TimeSpan.FromSeconds(2);
        Drain(client, time, buffer);
        Assert.IsTrue(client.SendPathMtuProbe(confirm: true));
        int size = client.PollTransmit(time, buffer, out _);
        Assert.IsTrue(size > QuicPathMtu.Base);
        ulong newest = 0;
        for (int extra = 0; extra < 3; extra++)
        {
            client.SendKeepAlive();
            Assert.IsTrue(client.PollTransmit(time, buffer, out _) > 0);
            newest = client.LastSentApplication;
        }

        client.AcknowledgeApplication(newest, time + TimeSpan.FromMilliseconds(1));
    }

    private static void Drain(QuicConnectionCore client, DateTimeOffset time, byte[] buffer)
    {
        while (client.PollTransmit(time, buffer, out _) > 0)
        {
        }
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
