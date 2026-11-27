#pragma warning disable CA1416 // The test names the BCL stream type. It does not open a platform listener.
#pragma warning disable CA2022 // These reads assert a zero return or a cancel, not a filled buffer.
using System;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class ManagedQuicStreamEdgeTests
{
    [TestMethod]
    public async Task SynchronousMembersThrowAndAnEmptyWriteSendsNothing()
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
        Assert.IsTrue(outbound.CanRead);
        Assert.IsFalse(outbound.CanSeek);
        Assert.IsTrue(outbound.CanWrite);
        Assert.AreEqual(0L, outbound.Id);
        Assert.AreEqual(QuicStreamType.Bidirectional, outbound.Type);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = outbound.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = outbound.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => outbound.Position = 1);
        NotSupportedException read = Assert.ThrowsExactly<NotSupportedException>(() => outbound.Read(new byte[1], 0, 1));
        Assert.AreEqual("Use ReadAsync.", read.Message);
        NotSupportedException write = Assert.ThrowsExactly<NotSupportedException>(() => outbound.Write(new byte[1], 0, 1));
        Assert.AreEqual("Use WriteAsync.", write.Message);
        Assert.ThrowsExactly<NotSupportedException>(() => outbound.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => outbound.SetLength(1));
        outbound.Flush();
        await outbound.WriteAsync(ReadOnlyMemory<byte>.Empty, cancel.Token);

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await outbound.WriteAsync(new byte[] { 1 }, stopped.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await inbound.ReadAsync(new byte[1], stopped.Token));

        await client.CloseAsync(0, cancel.Token);
        int count = await inbound.ReadAsync(new byte[8], cancel.Token);
        Assert.AreEqual(0, count);
        await server.CloseAsync(0, cancel.Token);
    }

    private static X509Certificate2 Certificate()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
