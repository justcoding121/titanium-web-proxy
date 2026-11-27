#pragma warning disable CA1416 // The test names the BCL error type. It does not open a platform listener.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class ManagedQuicListenerEdgeTests
{
    [TestMethod]
    public async Task AListenWithoutAnEndpointOrATokenDoesNotBind()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await ManagedQuicListener.ListenAsync(null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions()));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions()),
        }, canceled.Token));
    }

    [TestMethod]
    public async Task ACanceledWaiterIsDroppedAndTheNextAcceptReceives()
    {
        using X509Certificate2 certificate = Certificate();
        await using ManagedQuicListener listener = await Listen(certificate, TimeSpan.Zero);
        using var firstCancel = new CancellationTokenSource();
        using var secondCancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<ManagedQuicConnection> first = listener.AcceptConnectionAsync(firstCancel.Token).AsTask();
        Task<ManagedQuicConnection> second = listener.AcceptConnectionAsync(secondCancel.Token).AsTask();
        firstCancel.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await first);
        await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(Client(listener.LocalEndPoint), secondCancel.Token);
        await using ManagedQuicConnection server = await second;
        Assert.AreEqual(listener.LocalEndPoint, client.RemoteEndPoint);
        await client.CloseAsync(0, secondCancel.Token);
        await server.CloseAsync(0, secondCancel.Token);
    }

    [TestMethod]
    public async Task ABrokenCallbackAndAMissingCertificateFailTheAccept()
    {
        await using ManagedQuicListener broken = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => throw new InvalidOperationException("no certificate choice"),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<ManagedQuicConnection> accepted = broken.AcceptConnectionAsync(cancel.Token).AsTask();
        Task connect = ConnectQuietly(broken.LocalEndPoint, cancel.Token);
        QuicException callback = await Assert.ThrowsExactlyAsync<QuicException>(async () => await accepted);
        Assert.AreEqual(QuicError.CallbackError, callback.QuicError);
        Assert.AreEqual("no certificate choice", callback.Message);
        await connect;

        await using ManagedQuicListener missing = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions()),
        });
        Task<ManagedQuicConnection> missingAccept = missing.AcceptConnectionAsync(cancel.Token).AsTask();
        Task missingConnect = ConnectQuietly(missing.LocalEndPoint, cancel.Token);
        QuicException certificate = await Assert.ThrowsExactlyAsync<QuicException>(async () => await missingAccept);
        Assert.AreEqual(QuicError.CallbackError, certificate.QuicError);
        Assert.AreEqual("The server certificate is missing.", certificate.Message);
        await missingConnect;
    }

    [TestMethod]
    public async Task ASlowCallbackTimesOutAndAContextCertificateConnects()
    {
        using X509Certificate2 certificate = Certificate();
        await using ManagedQuicListener slow = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            HandshakeTimeout = TimeSpan.FromMilliseconds(50),
            ConnectionOptionsCallback = async (_, _, token) =>
            {
                await Task.Delay(300, token);
                return new ManagedQuicServerConnectionOptions
                {
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
                };
            },
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<ManagedQuicConnection> accepted = slow.AcceptConnectionAsync(cancel.Token).AsTask();
        Task connect = ConnectQuietly(slow.LocalEndPoint, cancel.Token);
        QuicException timed = await Assert.ThrowsExactlyAsync<QuicException>(async () => await accepted);
        Assert.AreEqual(QuicError.ConnectionTimeout, timed.QuicError);
        Assert.AreEqual("The handshake timed out.", timed.Message);
        await connect;

        string? serverName = null;
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            HandshakeTimeout = TimeSpan.Zero,
            ConnectionOptionsCallback = async (_, hello, token) =>
            {
                serverName = hello.ServerName;
                await Task.Delay(100, token);
                return new ManagedQuicServerConnectionOptions
                {
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = SslStreamCertificateContext.Create(certificate, null),
                    },
                };
            },
        });
        Task<ManagedQuicConnection> contextAccept = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(Client(listener.LocalEndPoint), cancel.Token);
        await using ManagedQuicConnection server = await contextAccept;
        Assert.AreEqual("example.test", serverName);
        await client.CloseAsync(0, cancel.Token);
        await server.CloseAsync(0, cancel.Token);
    }

    [TestMethod]
    public async Task AFinishedHandshakeWaitsInTheQueueUntilAccept()
    {
        using X509Certificate2 certificate = Certificate();
        await using ManagedQuicListener listener = await Listen(certificate, TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(Client(listener.LocalEndPoint), cancel.Token);
        await Task.Delay(50, cancel.Token);
        await using ManagedQuicConnection server = await listener.AcceptConnectionAsync(cancel.Token);
        Assert.AreEqual(listener.LocalEndPoint, client.RemoteEndPoint);
        await client.CloseAsync(0, cancel.Token);
        await server.CloseAsync(0, cancel.Token);
    }

    [TestMethod]
    public async Task AProtocolTheClientDidNotOfferStopsTheHandshake()
    {
        using X509Certificate2 certificate = Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new("nope") },
                },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        ManagedQuicClientConnectionOptions client = Client(listener.LocalEndPoint);
        client.ClientAuthenticationOptions!.ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") };
        async Task ConnectOffered()
        {
            try
            {
                await using ManagedQuicConnection connection = await ManagedQuicConnection.ConnectAsync(client, cancel.Token);
            }
            catch (QuicException)
            {
            }
        }

        Task connect = ConnectOffered();
        QuicException stopped = await Assert.ThrowsExactlyAsync<QuicException>(async () => await accepted);
        Assert.AreEqual(QuicError.ConnectionAborted, stopped.QuicError);
        Assert.AreEqual("The handshake did not continue.", stopped.Message);
        await connect;
    }

    [TestMethod]
    public async Task AResetPeerDoesNotStopTheListener()
    {
        using X509Certificate2 certificate = Certificate();
        await using ManagedQuicListener listener = await Listen(certificate, TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var handshake = new CancellationTokenSource();
        ValueTask<ManagedQuicConnection> connecting = ManagedQuicConnection.ConnectAsync(Client(listener.LocalEndPoint), handshake.Token);
        handshake.Cancel();
        try
        {
            await using ManagedQuicConnection abandoned = await connecting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuicException)
        {
        }

        await Task.Delay(50, cancel.Token);
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(Client(listener.LocalEndPoint), cancel.Token);
        await using ManagedQuicConnection server = await accepted;
        await client.CloseAsync(0, cancel.Token);
        await server.CloseAsync(0, cancel.Token);
    }

    [TestMethod]
    public async Task JunkIsIgnoredAndDisposeCancelsTheWait()
    {
        using X509Certificate2 certificate = Certificate();
        ManagedQuicListener listener = await Listen(certificate, TimeSpan.FromSeconds(10));
        using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            udp.Connect(listener.LocalEndPoint);
            udp.Send(new byte[] { 0x40, 0x01 });
            udp.Send(new byte[] { 0xC0 });
        }

        Task<ManagedQuicConnection> pending = listener.AcceptConnectionAsync().AsTask();
        await listener.DisposeAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);
    }

    private static async Task ConnectQuietly(IPEndPoint remote, CancellationToken cancellationToken)
    {
        try
        {
            await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(Client(remote), cancellationToken);
        }
        catch (QuicException)
        {
        }
    }

    private static ManagedQuicClientConnectionOptions Client(IPEndPoint remote) => new()
    {
        RemoteEndPoint = remote,
        HandshakeTimeout = TimeSpan.FromSeconds(2),
        ClientAuthenticationOptions = new SslClientAuthenticationOptions { TargetHost = "example.test" },
    };

    private static ValueTask<ManagedQuicListener> Listen(X509Certificate2 certificate, TimeSpan handshakeTimeout) =>
        ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            HandshakeTimeout = handshakeTimeout,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
            }),
        });

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
