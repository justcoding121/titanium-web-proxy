#pragma warning disable CA1416 // QuicException is the BCL error type. These tests do not start the platform QUIC stack.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicConnectionSessionTests
{
    [TestMethod]
    public void StaticHelpersKeepTheIdLengthAndTheServerParameter()
    {
        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        PathInfo path = QuicConnectionSession.PathOf(local, remote);
        Assert.IsTrue(path.Equals(new PathInfo(local.Address.GetAddressBytes(), 9, remote.Address.GetAddressBytes(), 10)));
        Assert.IsFalse(path.Equals(new PathInfo(remote.Address.GetAddressBytes(), 10, local.Address.GetAddressBytes(), 9)));

        byte[] id = QuicConnectionSession.NewConnectionId();
        Assert.AreEqual(QuicConnectionSession.ConnectionIdLength, id.Length);
        byte[] client = QuicConnectionSession.Parameters(client: true, id);
        byte[] server = QuicConnectionSession.Parameters(client: false, id);
        Assert.IsTrue(client.Length > 0);
        Assert.IsTrue(server.Length > client.Length);

        Assert.IsNull(QuicConnectionSession.ProtocolNames(null));
        Assert.IsNull(QuicConnectionSession.ProtocolNames(new List<SslApplicationProtocol>()));
        string[] names = QuicConnectionSession.ProtocolNames([new SslApplicationProtocol("h3")])!;
        CollectionAssert.AreEqual(new[] { "h3" }, names);

        QuicConnectionSession.RejectFips(allow: true);
        QuicConnectionSession.RejectFips(allow: false);
    }

    [TestMethod]
    public async Task AClosedSessionRejectsAWriteAndASecondDisposeIsQuiet()
    {
        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        using var core = new QuicConnectionCore();
        var transport = new FakeTransport();
        var session = new QuicConnectionSession(core, transport, local, remote, ownsTransport: false);
        byte[] localId = QuicConnectionSession.NewConnectionId();
        core.StartClient(QuicConnectionSession.PathOf(local, remote), localId, QuicConnectionSession.NewConnectionId(), QuicConnectionSession.Parameters(client: true, localId));

        session.SetRemote(new IPEndPoint(IPAddress.Loopback, 11));
        Assert.AreEqual(11, session.RemoteEndPoint.Port);
        Assert.IsFalse(session.TakePendingHello(out string serverName));
        Assert.IsTrue(string.IsNullOrEmpty(serverName));
        using X509Certificate2 certificate = Certificate();
        Assert.IsFalse(session.CompleteHello(certificate));
        Assert.IsFalse(session.IsClosed);
        Assert.AreEqual(0, session.CopyReceived(0, []));
        Assert.AreEqual(0, session.CopyReceived(4, new byte[8]));
        session.Timeout(DateTimeOffset.MinValue);

        var stream = new ManagedQuicStream(session, 0, QuicStreamType.Bidirectional);
        session.AddStream(stream);
        session.NotifyStreams();
        await session.FlushAsync();
        DateTimeOffset? due = session.NextTimeout();
        Assert.IsTrue(due.HasValue);
        session.Timeout(due.Value);
        await session.FlushAsync();
        Assert.IsTrue(transport.Sends >= 2);
        Assert.AreEqual(0, transport.Batches);

        QuicException rejected = Assert.ThrowsExactly<QuicException>(() => session.Send(0, [1, 2, 3]));
        Assert.AreEqual(QuicError.InternalError, rejected.QuicError);
        Assert.AreEqual("The stream did not accept the write.", rejected.Message);
        session.Close(-1);
        Assert.IsTrue(session.IsClosed);
        session.SignalClosedHandshake();
        Assert.IsTrue(session.Handshake.IsFaulted);
        Assert.AreEqual("The connection closed before the handshake completed.", session.Handshake.Exception!.InnerException!.Message);
        QuicException closed = Assert.ThrowsExactly<QuicException>(() => session.Send(0, [1]));
        Assert.AreEqual(QuicError.ConnectionAborted, closed.QuicError);
        Assert.AreEqual("The connection closed with error 0x0.", closed.Message);

        session.Dispose();
        session.Dispose();
        Assert.AreEqual(0, transport.Disposed);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task TheClientLoopStopsWhenTheSessionStops()
    {
        var local = new IPEndPoint(IPAddress.Loopback, 9);
        var remote = new IPEndPoint(IPAddress.Loopback, 10);
        using var core = new QuicConnectionCore();
        var transport = new FakeTransport();
        var session = new QuicConnectionSession(core, transport, local, remote, ownsTransport: true);
        byte[] localId = QuicConnectionSession.NewConnectionId();
        core.StartClient(QuicConnectionSession.PathOf(local, remote), localId, QuicConnectionSession.NewConnectionId(), QuicConnectionSession.Parameters(client: true, localId));
        session.StartClientLoop();
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (transport.Receives < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(transport.Receives >= 3);
        await session.StopAsync();
        Assert.IsTrue(transport.Sends >= 1);
        Assert.AreEqual(1, transport.Disposed);
        session.Dispose();
    }

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class FakeTransport : IUdpPacketTransport
    {
        public int Sends;

        public int Batches;

        public int Receives;

        public int Disposed;

        public IPEndPoint LocalEndpoint { get; } = new(IPAddress.Loopback, 9);

        public int MaximumDatagramSize => 65507;

        public bool DatagramPending => false;

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken cancellationToken)
        {
            Sends++;
            return ValueTask.FromResult(datagram.Length);
        }

        public ValueTask<int> SendBatchAsync(ReadOnlyMemory<ReadOnlyMemory<byte>> datagrams, IPEndPoint destination, CancellationToken cancellationToken)
        {
            Batches++;
            return ValueTask.FromResult(datagrams.Length);
        }

        public async ValueTask<UdpReceived> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            Receives++;
            if (Receives == 1 && buffer.Length > 0)
            {
                buffer.Span[0] = 0;
                return new UdpReceived(1, new IPEndPoint(IPAddress.Loopback, 10));
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new UdpReceived(0, new IPEndPoint(IPAddress.Loopback, 10));
        }

        public void Dispose() => Disposed++;
    }
}
