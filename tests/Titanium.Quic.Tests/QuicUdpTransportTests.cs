using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicUdpTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly byte[] ClientId = [0xC1, 0xC1, 0xC1, 0xC1];

    private static readonly byte[] ServerId = [0x51, 0x51, 0x51, 0x51];

    [TestMethod]
    public async Task TwoLoopbackSocketsExchangeOneDatagram()
    {
        using var left = new UdpSocketTransport();
        using var right = new UdpSocketTransport();
        AssertEphemeral(left.LocalEndpoint.Port);
        AssertEphemeral(right.LocalEndpoint.Port);
        Assert.IsTrue(left.MaximumDatagramSize >= 1200);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] payload = [1, 2, 3, 4];
        byte[] buffer = new byte[2048];
        ValueTask<UdpReceived> pending = right.ReceiveAsync(buffer, cancel.Token);
        int sent = await left.SendAsync(payload, right.LocalEndpoint, cancel.Token);
        UdpReceived received = await pending;
        Assert.AreEqual(payload.Length, sent);
        Assert.AreEqual(payload.Length, received.Count);
        Assert.AreEqual(1, buffer[0]);
        Assert.AreEqual(4, buffer[3]);
        Assert.AreEqual(left.LocalEndpoint.Port, received.Remote.Port);
        Assert.AreEqual(IPAddress.Loopback, received.Remote.Address);
        Assert.IsTrue(left.TrySetEcn(0));
        Assert.IsFalse(left.TrySetEcn(3));
        Assert.IsFalse(left.TrySetEcn(4));
    }

    [TestMethod]
    public async Task BatchSizeFamilyAndAnOversizedDatagramAreEdges()
    {
        using var left = new UdpSocketTransport();
        using var right = new UdpSocketTransport();
        Assert.AreEqual(65507, left.MaximumDatagramSize);
        Assert.IsFalse(left.DatagramPending);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => left.SendAsync(new byte[left.MaximumDatagramSize + 1], right.LocalEndpoint, cancel.Token).AsTask());

        byte[] first = [9];
        byte[] second = [8, 7];
        ReadOnlyMemory<byte>[] batch = [first, second];
        byte[] firstBuffer = new byte[16];
        byte[] secondBuffer = new byte[16];
        ValueTask<UdpReceived> pendingFirst = right.ReceiveAsync(firstBuffer, cancel.Token);
        ValueTask<UdpReceived> pendingSecond = right.ReceiveAsync(secondBuffer, cancel.Token);
        int sent = await left.SendBatchAsync(batch, right.LocalEndpoint, cancel.Token);
        Assert.AreEqual(first.Length + second.Length, sent);
        UdpReceived gotFirst = await pendingFirst;
        UdpReceived gotSecond = await pendingSecond;
        Assert.AreEqual(first.Length, gotFirst.Count);
        Assert.AreEqual(second.Length, gotSecond.Count);
        Assert.AreEqual(0, await left.SendBatchAsync(ReadOnlyMemory<ReadOnlyMemory<byte>>.Empty, right.LocalEndpoint, cancel.Token));

        await left.SendAsync(first, right.LocalEndpoint, cancel.Token);
        Assert.IsTrue(right.DatagramPending);

        using var left6 = new UdpSocketTransport(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        using var right6 = new UdpSocketTransport(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        Assert.AreEqual(65527, left6.MaximumDatagramSize);
        byte[] payload = [5, 6];
        byte[] buffer = new byte[32];
        ValueTask<UdpReceived> pending = right6.ReceiveAsync(buffer, cancel.Token);
        int sent6 = await left6.SendAsync(payload, right6.LocalEndpoint, cancel.Token);
        UdpReceived got = await pending;
        Assert.AreEqual(payload.Length, sent6);
        Assert.AreEqual(payload.Length, got.Count);
        Assert.AreEqual(left6.LocalEndpoint.Port, got.Remote.Port);
        Assert.AreEqual(IPAddress.IPv6Loopback, got.Remote.Address);

        byte[] exact = new byte[left.MaximumDatagramSize];
        exact[0] = 1;
        exact[^1] = 2;
        Assert.AreEqual(exact.Length, await left.SendAsync(exact, right.LocalEndpoint, cancel.Token));
        Assert.IsTrue(left6.TrySetEcn(0));
        left.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => left.SendAsync(first, right.LocalEndpoint, cancel.Token).AsTask());
    }

    [TestMethod]
    public void InjectDeliversTheDatagramAndTheCallbackSeesTheReply()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new QuicConnectionCore();
        using var server = new QuicConnectionCore();
        var clientPath = new PathInfo([10, 0, 0, 1], 443, [10, 0, 0, 2], 50000);
        client.StartClient(clientPath, ClientId, ServerId, Parameters(client: true));
        server.StartServer(ServerId, certificate, Parameters(client: false));
        byte[] outbound = new byte[16384];
        int count = client.PollTransmit(Now, outbound, out _);
        Assert.IsTrue(count > 0);
        var virtualPath = new PathInfo([127, 0, 0, 1], 1, [127, 0, 0, 2], 2);
        var capture = new Capture();
        byte[] reply = new byte[16384];
        int replies = QuicDatagramInject.Deliver(server, Now, outbound.AsSpan(0, count), virtualPath, reply, ref capture);
        Assert.IsTrue(replies > 0);
        Assert.IsTrue(capture.Count > 0);
        Assert.IsTrue(capture.Length > 0);
        Assert.IsTrue(capture.Path.Equals(virtualPath));
        Assert.AreEqual(count, server.BytesReceived(virtualPath));
    }

    private static void AssertEphemeral(int port)
    {
        Assert.AreNotEqual(0, port);
        Assert.AreNotEqual(8000, port);
        Assert.AreNotEqual(8866, port);
        Assert.AreNotEqual(9080, port);
        Assert.AreNotEqual(9464, port);
    }

    private struct Capture : IQuicOutbound
    {
        public int Count;

        public int Length;

        public PathInfo Path;

        public void OnDatagram(ReadOnlySpan<byte> datagram, PathInfo path)
        {
            Count++;
            Length = datagram.Length;
            Path = path;
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
