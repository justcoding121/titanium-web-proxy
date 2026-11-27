#pragma warning disable CA1416 // The test names the BCL stream type. It does not open a platform listener.
using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class ManagedQuicConnectionEdgeTests
{
    [TestMethod]
    public async Task AConnectWithoutAnEndpointOrATokenDoesNotOpen()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await ManagedQuicConnection.ConnectAsync(null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await ManagedQuicConnection.ConnectAsync(new ManagedQuicClientConnectionOptions()));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await ManagedQuicConnection.ConnectAsync(
            new ManagedQuicClientConnectionOptions { RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 9) },
            canceled.Token));
    }

    [TestMethod]
    public async Task OneConnectionOpensAndAcceptsASingleBidirectionalStream()
    {
        using X509Certificate2 certificate = Certificate();
        string? serverName = null;
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, hello, _) =>
            {
                serverName = hello.ServerName;
                return ValueTask.FromResult(new ManagedQuicServerConnectionOptions
                {
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
                });
            },
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int localPort = FreePort();
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(new ManagedQuicClientConnectionOptions
        {
            RemoteEndPoint = listener.LocalEndPoint,
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
            HandshakeTimeout = TimeSpan.Zero,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions { TargetHost = string.Empty },
        }, cancel.Token);
        Assert.AreEqual(localPort, client.LocalEndPoint.Port);
        await using ManagedQuicConnection server = await accepted;
        Assert.AreEqual("localhost", serverName);
        Assert.AreEqual(listener.LocalEndPoint, client.RemoteEndPoint);

        using var stopOpen = new CancellationTokenSource();
        stopOpen.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stopOpen.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await server.AcceptInboundStreamAsync(stopOpen.Token));
        QuicException unidirectional = await Assert.ThrowsExactlyAsync<QuicException>(async () => await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancel.Token));
        Assert.AreEqual("Only a bidirectional stream can be opened.", unidirectional.Message);
        ManagedQuicStream outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancel.Token);
        QuicException second = await Assert.ThrowsExactlyAsync<QuicException>(async () => await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancel.Token));
        Assert.AreEqual("This connection opens one bidirectional stream.", second.Message);
        ManagedQuicStream inbound = await server.AcceptInboundStreamAsync(cancel.Token);
        QuicException secondAccept = await Assert.ThrowsExactlyAsync<QuicException>(async () => await server.AcceptInboundStreamAsync(cancel.Token));
        Assert.AreEqual("This connection accepts one bidirectional stream.", secondAccept.Message);
        Assert.AreEqual(0L, outbound.Id);
        Assert.AreEqual(0L, inbound.Id);

        using var closed = new CancellationTokenSource();
        closed.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await client.CloseAsync(1, closed.Token));
        await client.CloseAsync(0, cancel.Token);
    }

    [TestMethod]
    public async Task ASilentPeerTimesOutAndAnUnfinishedHandshakeOpensNothing()
    {
        using var sink = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sink.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)sink.LocalEndPoint!).Port;
        QuicException timed = await Assert.ThrowsExactlyAsync<QuicException>(async () => await ManagedQuicConnection.ConnectAsync(new ManagedQuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            HandshakeTimeout = TimeSpan.FromMilliseconds(200),
        }));
        Assert.AreEqual(QuicError.ConnectionTimeout, timed.QuicError);
        Assert.AreEqual("The handshake timed out.", timed.Message);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        QuicException fast = await Assert.ThrowsExactlyAsync<QuicException>(async () => await ManagedQuicConnection.ConnectAsync(new ManagedQuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            HandshakeTimeout = TimeSpan.FromMilliseconds(50),
        }, caller.Token));
        Assert.AreEqual(QuicError.ConnectionTimeout, fast.QuicError);

        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        using var core = new QuicConnectionCore();
        using var transport = new IdleTransport();
        var session = new QuicConnectionSession(core, transport, local, remote, ownsTransport: false);
        await using var connection = new ManagedQuicConnection(session);
        QuicException open = await Assert.ThrowsExactlyAsync<QuicException>(async () => await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional));
        Assert.AreEqual("The handshake is not complete.", open.Message);
        QuicException accept = await Assert.ThrowsExactlyAsync<QuicException>(async () => await connection.AcceptInboundStreamAsync());
        Assert.AreEqual("The handshake is not complete.", accept.Message);
    }

    [TestMethod]
    public async Task AnIpv6LoopbackConnects()
    {
        using X509Certificate2 certificate = Certificate();
        string? serverName = null;
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.IPv6Loopback, 0),
            ConnectionOptionsCallback = (_, hello, _) =>
            {
                serverName = hello.ServerName;
                return ValueTask.FromResult(new ManagedQuicServerConnectionOptions
                {
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
                });
            },
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(new ManagedQuicClientConnectionOptions
        {
            RemoteEndPoint = listener.LocalEndPoint,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions { TargetHost = "example.test" },
        }, cancel.Token);
        await using ManagedQuicConnection server = await accepted;
        Assert.AreEqual("example.test", serverName);
        Assert.AreEqual(AddressFamily.InterNetworkV6, client.LocalEndPoint.AddressFamily);
        await client.CloseAsync(0, cancel.Token);
        await server.CloseAsync(0, cancel.Token);
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class IdleTransport : IUdpPacketTransport
    {
        public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, 9);

        public int MaximumDatagramSize => 1200;

        public bool DatagramPending => false;

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken cancellationToken) => ValueTask.FromResult(0);

        public ValueTask<int> SendBatchAsync(ReadOnlyMemory<ReadOnlyMemory<byte>> datagrams, IPEndPoint destination, CancellationToken cancellationToken) => ValueTask.FromResult(0);

        public ValueTask<UdpReceived> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private static X509Certificate2 Certificate()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
