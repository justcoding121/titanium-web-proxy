#pragma warning disable CA1416 // The test names the BCL stream type. It does not open a platform listener.
using System;
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
public sealed class ManagedQuicTests
{
    [TestMethod]
    public async Task LoopbackConnectsOneBidirectionalStreamAndCloses()
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
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                    },
                });
            },
        });
        AssertEphemeral(listener.LocalEndPoint.Port);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(new ManagedQuicClientConnectionOptions
        {
            RemoteEndPoint = listener.LocalEndPoint,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions { TargetHost = "localhost" },
        }, cancel.Token);
        await using ManagedQuicConnection server = await accepted;
        Assert.AreEqual("localhost", serverName);
        AssertEphemeral(client.LocalEndPoint.Port);
        ManagedQuicStream outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancel.Token);
        ManagedQuicStream inbound = await server.AcceptInboundStreamAsync(cancel.Token);
        byte[] payload = RandomNumberGenerator.GetBytes(64);
        await outbound.WriteAsync(payload, cancel.Token);
        byte[] got = new byte[payload.Length];
        int read = 0;
        while (read < got.Length)
        {
            int count = await inbound.ReadAsync(got.AsMemory(read), cancel.Token);
            Assert.IsTrue(count > 0);
            read += count;
        }

        byte[] expected = SHA256.HashData(payload);
        byte[] actual = SHA256.HashData(got);
        Assert.IsTrue(expected.AsSpan().SequenceEqual(actual));
        await client.CloseAsync(0, cancel.Token);
        await server.CloseAsync(0, cancel.Token);
    }

    [TestMethod]
    public async Task AWriteLargerThanTheCongestionWindowIsDelivered()
    {
        using X509Certificate2 certificate = Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
            }),
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync(cancel.Token).AsTask();
        await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(new ManagedQuicClientConnectionOptions
        {
            RemoteEndPoint = listener.LocalEndPoint,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions { TargetHost = "localhost" },
        }, cancel.Token);
        await using ManagedQuicConnection server = await accepted;
        ManagedQuicStream outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancel.Token);
        ManagedQuicStream inbound = await server.AcceptInboundStreamAsync(cancel.Token);
        byte[] payload = RandomNumberGenerator.GetBytes(32 * 1024);
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
    }

    private static void AssertEphemeral(int port)
    {
        Assert.AreNotEqual(0, port);
        Assert.AreNotEqual(8000, port);
        Assert.AreNotEqual(8866, port);
        Assert.AreNotEqual(9080, port);
        Assert.AreNotEqual(9464, port);
    }

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
