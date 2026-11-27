using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class TlsStreamTests
{
    [TestMethod]
    public void SynchronousAuthenticationMovesOneRecord()
    {
        using X509Certificate2 certificate = Certificate();
        using TcpListener listener = Listen();
        byte[] payload = [1, 2, 3, 4];
        Task server = Task.Run(() =>
        {
            using TcpClient accepted = listener.AcceptTcpClient();
            using var stream = new TlsStream(accepted.GetStream());
            stream.AuthenticateAsServer(ServerOptions(certificate));
            byte[] buffer = new byte[4];
            Assert.AreEqual(4, stream.Read(buffer));
            stream.Write(buffer);
        });

        using TcpClient clientTcp = Connect(listener);
        using var client = new TlsStream(clientTcp.GetStream());
        client.AuthenticateAsClient(ClientOptions());
        client.Write(payload);
        byte[] echoed = new byte[4];
        Assert.AreEqual(4, client.Read(echoed));
        CollectionAssert.AreEqual(payload, echoed);
        server.GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task TransferMatchesOnBothSides()
    {
        byte[] outbound = Random(10 * 1024 * 1024);
        byte[] inbound = Random(10 * 1024 * 1024);
        using X509Certificate2 certificate = Certificate();
        (TlsStream Client, TlsStream Server) pair = await Pair(certificate, null);
        await using TlsStream client = pair.Client;
        await using TlsStream server = pair.Server;
        Task writeOut = client.WriteAsync(outbound).AsTask();
        Task writeIn = server.WriteAsync(inbound).AsTask();
        Task<byte[]> readOut = ReadExact(server, outbound.Length);
        Task<byte[]> readIn = ReadExact(client, inbound.Length);
        await Task.WhenAll(writeOut, writeIn, readOut, readIn);
        byte[] gotOut = await readOut;
        byte[] gotIn = await readIn;
        CollectionAssert.AreEqual(SHA256.HashData(outbound), SHA256.HashData(gotOut));
        CollectionAssert.AreEqual(SHA256.HashData(inbound), SHA256.HashData(gotIn));
        Assert.AreEqual(SslProtocols.Tls13, client.SslProtocol);
        Assert.AreEqual(TlsCipherSuite.TLS_AES_128_GCM_SHA256, client.NegotiatedCipherSuite);
        Assert.AreEqual("h2", client.NegotiatedApplicationProtocol.ToString());
        Assert.IsTrue(client.IsAuthenticated);
        Assert.IsTrue(client.IsEncrypted);
        Assert.IsTrue(server.IsServer);
        byte[] left = new byte[32];
        byte[] right = new byte[32];
        client.ExportKeyingMaterial(left, "EXPERIMENTAL", "ctx"u8);
        server.ExportKeyingMaterial(right, "EXPERIMENTAL", "ctx"u8);
        CollectionAssert.AreEqual(left, right);
    }

    [TestMethod]
    public async Task ZeroLengthReadDoesNotConsumeTheRecord()
    {
        using X509Certificate2 certificate = Certificate();
        (TlsStream Client, TlsStream Server) pair = await Pair(certificate, null);
        await using TlsStream client = pair.Client;
        await using TlsStream server = pair.Server;
        await client.WriteAsync(new byte[] { 9, 8, 7, 6 });
        Assert.AreEqual(0, server.Read(Span<byte>.Empty));
        byte[] buffer = new byte[4];
        Assert.AreEqual(4, server.Read(buffer));
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, buffer);
        client.Write([]);
    }

    [TestMethod]
    public async Task TransportEndWithoutCloseNotifyThrows()
    {
        using X509Certificate2 certificate = Certificate();
        (TlsStream Client, TlsStream Server) pair = await Pair(certificate, null);
        await using TlsStream client = pair.Client;
        await pair.Server.DisposeAsync();
        Assert.ThrowsException<IOException>(() => client.Read(new byte[4]));
    }

    [TestMethod]
    public async Task ReadAfterDisposeThrows()
    {
        using X509Certificate2 certificate = Certificate();
        (TlsStream Client, TlsStream Server) pair = await Pair(certificate, null);
        await pair.Server.DisposeAsync();
        await pair.Client.DisposeAsync();
        Assert.ThrowsException<ObjectDisposedException>(() => pair.Client.Read(new byte[1]));
    }

    [TestMethod]
    public async Task WriteAfterShutdownThrows()
    {
        using X509Certificate2 certificate = Certificate();
        (TlsStream Client, TlsStream Server) pair = await Pair(certificate, null);
        await using TlsStream client = pair.Client;
        await using TlsStream server = pair.Server;
        await client.ShutdownAsync();
        InvalidOperationException error = Assert.ThrowsException<InvalidOperationException>(() => client.Write(new byte[] { 1 }));
        Assert.AreEqual("Write operations are not allowed after the channel was shutdown.", error.Message);
        byte[] buffer = new byte[1];
        Assert.AreEqual(0, server.Read(buffer));
    }

    [TestMethod]
    public async Task ConcurrentWriteAndReadMovesFourBytes()
    {
        using X509Certificate2 certificate = Certificate();
        (TlsStream Client, TlsStream Server) pair = await Pair(certificate, null);
        await using TlsStream client = pair.Client;
        await using TlsStream server = pair.Server;
        byte[] payload = [4, 5, 6, 7];
        byte[] got = new byte[4];
        Task<int> reading = server.ReadAsync(got).AsTask();
        await client.WriteAsync(payload);
        Assert.AreEqual(4, await reading);
        CollectionAssert.AreEqual(payload, got);
    }

    [TestMethod]
    public async Task CancelledReadThrows()
    {
        using X509Certificate2 certificate = Certificate();
        (TlsStream Client, TlsStream Server) pair = await Pair(certificate, null);
        await using TlsStream client = pair.Client;
        await using TlsStream server = pair.Server;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
        {
            int read = await client.ReadAsync(new byte[4], cancellation.Token);
            Assert.AreEqual(-1, read);
        });
        await Assert.ThrowsExceptionAsync<IOException>(async () =>
        {
            int read = await client.ReadAsync(new byte[1]);
            Assert.AreEqual(-1, read);
        });
    }

    [TestMethod]
    public async Task KeyUpdateSurvivesConcurrentTraffic()
    {
        using X509Certificate2 certificate = Certificate();
        (TlsStream Client, TlsStream Server) pair = await Pair(certificate, null);
        await using TlsStream client = pair.Client;
        await using TlsStream server = pair.Server;
        for (int i = 0; i < 8; i++)
        {
            client.UpdateKeys();
            byte[] payload = [(byte)i, 2, 3, 4];
            byte[] got = new byte[4];
            Task<int> reading = server.ReadAsync(got).AsTask();
            await Task.Delay(1);
            await client.WriteAsync(payload);
            Assert.AreEqual(4, await reading);
            CollectionAssert.AreEqual(payload, got);
            server.UpdateKeys();
            byte[] reply = [(byte)(i + 10), 6, 7, 8];
            byte[] echoed = new byte[4];
            Task<int> clientRead = client.ReadAsync(echoed).AsTask();
            await server.WriteAsync(reply);
            Assert.AreEqual(4, await clientRead);
            CollectionAssert.AreEqual(reply, echoed);
        }
    }

    [TestMethod]
    public async Task KeyUpdateKeepsTheFollowingRecordReadable()
    {
        using X509Certificate2 certificate = Certificate();
        (TlsStream Client, TlsStream Server) pair = await Pair(certificate, null);
        await using TlsStream client = pair.Client;
        await using TlsStream server = pair.Server;
        await client.WriteAsync(new byte[] { 1 });
        Assert.AreEqual(1, server.Read(new byte[1]));
        client.UpdateKeys();
        await client.WriteAsync(new byte[] { 2 });
        byte[] second = new byte[1];
        Assert.AreEqual(1, server.Read(second));
        Assert.AreEqual(2, second[0]);
        server.UpdateKeys();
        await server.WriteAsync(new byte[] { 3 });
        byte[] reply = new byte[1];
        Assert.AreEqual(1, client.Read(reply));
        Assert.AreEqual(3, reply[0]);
    }

    [TestMethod]
    public async Task ClientSpeaksToAnSslStreamServer()
    {
        using X509Certificate2 certificate = Certificate();
        using TcpListener listener = Listen();
        Task server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using var stream = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false, static (_, _, _, _) => true);
            await stream.AuthenticateAsServerAsync(ServerOptions(certificate));
            byte[] buffer = new byte[4];
            Assert.AreEqual(4, await stream.ReadAsync(buffer));
            await stream.WriteAsync(buffer);
        });

        using TcpClient clientTcp = Connect(listener);
        await using var client = new TlsStream(clientTcp.GetStream());
        await client.AuthenticateAsClientAsync(ClientOptions());
        await client.WriteAsync(new byte[] { 1, 2, 3, 4 });
        byte[] echoed = new byte[4];
        Assert.AreEqual(4, await client.ReadAsync(echoed));
        server.GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task ServerSpeaksToAnSslStreamClient()
    {
        using X509Certificate2 certificate = Certificate();
        using TcpListener listener = Listen();
        Task server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using var stream = new TlsStream(accepted.GetStream());
            await stream.AuthenticateAsServerAsync(ServerOptions(certificate));
            byte[] buffer = new byte[4];
            Assert.AreEqual(4, await stream.ReadAsync(buffer));
            await stream.WriteAsync(buffer);
        });

        using TcpClient clientTcp = Connect(listener);
        await using var client = new SslStream(clientTcp.GetStream());
        await client.AuthenticateAsClientAsync(ClientOptions());
        await client.WriteAsync(new byte[] { 5, 6, 7, 8 });
        byte[] echoed = new byte[4];
        Assert.AreEqual(4, await client.ReadAsync(echoed));
        server.GetAwaiter().GetResult();
    }

    private static async Task<(TlsStream Client, TlsStream Server)> Pair(X509Certificate2 certificate, Action<SslServerAuthenticationOptions>? configure)
    {
        TcpListener listener = Listen();
        Task<TlsStream> accepted = AcceptServer(listener, certificate, configure);
        TcpClient clientTcp = Connect(listener);
        var client = new TlsStream(clientTcp.GetStream());
        await client.AuthenticateAsClientAsync(ClientOptions());
        TlsStream server = await accepted;
        listener.Stop();
        return (client, server);
    }

    private static async Task<TlsStream> AcceptServer(TcpListener listener, X509Certificate2 certificate, Action<SslServerAuthenticationOptions>? configure)
    {
        TcpClient accepted = await listener.AcceptTcpClientAsync();
        var stream = new TlsStream(accepted.GetStream());
        SslServerAuthenticationOptions options = ServerOptions(certificate);
        configure?.Invoke(options);
        await stream.AuthenticateAsServerAsync(options);
        return stream;
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static TcpClient Connect(TcpListener listener)
    {
        var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        return client;
    }

    private static SslClientAuthenticationOptions ClientOptions() => new()
    {
        TargetHost = "localhost",
        EnabledSslProtocols = SslProtocols.Tls13,
        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        ApplicationProtocols = [SslApplicationProtocol.Http2],
    };

    private static SslServerAuthenticationOptions ServerOptions(X509Certificate2 certificate) => new()
    {
        ServerCertificate = certificate,
        EnabledSslProtocols = SslProtocols.Tls13,
        ApplicationProtocols = [SslApplicationProtocol.Http2],
    };

    private static async Task<byte[]> ReadExact(Stream stream, int count)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset));
            Assert.AreNotEqual(0, read);
            offset += read;
        }

        return buffer;
    }

    private static byte[] Random(int count)
    {
        var buffer = new byte[count];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
    }

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }
}
