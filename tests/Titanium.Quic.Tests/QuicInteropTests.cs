#pragma warning disable CA1416 // The platform listener is started only off Windows, and only when TITANIUM_QUIC_PLATFORM_LISTENER is 1.
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
public sealed class QuicInteropTests
{
    [TestMethod]
    public async Task APlatformClientReachesTheManagedListener()
    {
        if (!QuicConnection.IsSupported)
        {
            Assert.Inconclusive("The platform QUIC client is not supported on this machine.");
        }

        using X509Certificate2 certificate = Certificate();
        var protocol = new SslApplicationProtocol("test");
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { protocol },
                },
            }),
        });
        Assert.AreNotEqual(0, listener.LocalEndPoint.Port);
        Assert.IsFalse(listener.LocalEndPoint.Port is 8000 or 8866 or 9080 or 9464);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = listener.LocalEndPoint,
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            HandshakeTimeout = TimeSpan.FromSeconds(10),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = new List<SslApplicationProtocol> { protocol },
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };
        await using QuicConnection platform = await QuicConnection.ConnectAsync(clientOptions, cancel.Token);
        await using ManagedQuicConnection server = await accepted;
        await using QuicStream outbound = await platform.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancel.Token);
        ManagedQuicStream inbound = await server.AcceptInboundStreamAsync(cancel.Token);
        byte[] payload = RandomNumberGenerator.GetBytes(32);
        await outbound.WriteAsync(payload, cancel.Token);
        byte[] got = new byte[payload.Length];
        int read = 0;
        while (read < got.Length)
        {
            int count = await inbound.ReadAsync(got.AsMemory(read), cancel.Token);
            Assert.IsTrue(count > 0);
            read += count;
        }

        Assert.IsTrue(SHA256.HashData(payload).AsSpan().SequenceEqual(SHA256.HashData(got)));
        await platform.CloseAsync(0, cancel.Token);
        await server.CloseAsync(0, cancel.Token);
    }

    [TestMethod]
    public async Task AManagedClientReachesThePlatformListener()
    {
        bool allowed = !OperatingSystem.IsWindows()
            && string.Equals(Environment.GetEnvironmentVariable("TITANIUM_QUIC_PLATFORM_LISTENER"), "1", StringComparison.Ordinal);
        if (!allowed || !QuicListener.IsSupported)
        {
            return;
        }

        using X509Certificate2 certificate = Certificate();
        var protocol = new SslApplicationProtocol("test");
        await using QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = new List<SslApplicationProtocol> { protocol },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
                IdleTimeout = TimeSpan.FromMinutes(1),
                MaxInboundBidirectionalStreams = 4,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { protocol },
                },
            }),
        });
        Assert.AreNotEqual(0, listener.LocalEndPoint.Port);
        Assert.IsFalse(listener.LocalEndPoint.Port is 8000 or 8866 or 9080 or 9464);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<QuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(new ManagedQuicClientConnectionOptions
        {
            RemoteEndPoint = listener.LocalEndPoint,
            HandshakeTimeout = TimeSpan.FromSeconds(10),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = new List<SslApplicationProtocol> { protocol },
            },
        }, cancel.Token);
        await using QuicConnection server = await accepted;
        Task<QuicStream> inboundTask = server.AcceptInboundStreamAsync(cancel.Token).AsTask();
        ManagedQuicStream outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancel.Token);
        byte[] payload = RandomNumberGenerator.GetBytes(32);
        await outbound.WriteAsync(payload, cancel.Token);
        await using QuicStream inbound = await inboundTask;
        byte[] got = new byte[payload.Length];
        int read = 0;
        while (read < got.Length)
        {
            int count = await inbound.ReadAsync(got.AsMemory(read), cancel.Token);
            Assert.IsTrue(count > 0);
            read += count;
        }

        Assert.IsTrue(SHA256.HashData(payload).AsSpan().SequenceEqual(SHA256.HashData(got)));
        byte[] reply = RandomNumberGenerator.GetBytes(32);
        await inbound.WriteAsync(reply, cancel.Token);
        byte[] echoed = new byte[reply.Length];
        read = 0;
        while (read < echoed.Length)
        {
            int count = await outbound.ReadAsync(echoed.AsMemory(read), cancel.Token);
            Assert.IsTrue(count > 0);
            read += count;
        }

        Assert.IsTrue(SHA256.HashData(reply).AsSpan().SequenceEqual(SHA256.HashData(echoed)));
        await client.CloseAsync(0, cancel.Token);
        await server.CloseAsync(0, cancel.Token);
    }

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
