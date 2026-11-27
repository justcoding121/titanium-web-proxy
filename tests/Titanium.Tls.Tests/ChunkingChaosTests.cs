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
public sealed class ChunkingChaosTests
{
    [TestMethod]
    [Timeout(60000)]
    public async Task OneByteReadsStillEcho()
    {
        await Echo(64, maxChunk: 1, seed: 1);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task FullRecordSurvivesShortReads()
    {
        await Echo(TlsRecordProtector.MaxPlaintext, maxChunk: 3, seed: 2);
    }

    [TestMethod]
    public async Task EmptyWriteDoesNotCloseTheRecordLayer()
    {
        (TlsStream client, TlsStream server, TcpListener listener) open = await Open(maxChunk: 1, seed: 3);
        await using TlsStream client = open.client;
        await using TlsStream server = open.server;
        using TcpListener listener = open.listener;
        await client.WriteAsync(ReadOnlyMemory<byte>.Empty);
        await client.WriteAsync(new byte[] { 7 });
        byte[] got = new byte[1];
        Assert.AreEqual(1, await server.ReadAsync(got));
        Assert.AreEqual(7, got[0]);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task CampaignSelfTestCatchesAFlippedRecord()
    {
        using X509Certificate2 certificate = Certificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<Exception?> server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using var stream = new TlsStream(accepted.GetStream());
            try
            {
                await stream.AuthenticateAsServerAsync(ServerOptions(certificate));
                _ = await stream.ReadAsync(new byte[16]);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        });

        using var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, port);
        var corrupt = new CorruptOnceStream(tcp.GetStream());
        await using var client = new TlsStream(corrupt);
        await client.AuthenticateAsClientAsync(ClientOptions());
        corrupt.Arm();
        await client.WriteAsync(new byte[16]);
        Exception? caught = await server;
        Assert.IsInstanceOfType<IOException>(caught);
        var alert = caught.InnerException as TlsAlertException;
        Assert.IsNotNull(alert);
        Assert.AreEqual(20, alert.Description);
    }

    [TestMethod]
    public void RandomInputsDoNotThrow()
    {
        var random = new Random(8446);
        for (int i = 0; i < 200; i++)
        {
            var data = new byte[random.Next(0, 512)];
            random.NextBytes(data);
            TlsFuzzTarget.Handshake(data);
        }
    }

    private static async Task Echo(int bytes, int maxChunk, int seed)
    {
        (TlsStream client, TlsStream server, TcpListener listener) open = await Open(maxChunk, seed);
        await using TlsStream client = open.client;
        await using TlsStream server = open.server;
        using TcpListener listener = open.listener;
        var payload = new byte[bytes];
        payload.AsSpan().Fill(0x3C);
        var received = new byte[bytes];
        Task<int> read = ReadExact(server, received);
        await client.WriteAsync(payload);
        Assert.AreEqual(bytes, await read);
        await server.WriteAsync(received);
        var echoed = new byte[bytes];
        Assert.AreEqual(bytes, await ReadExact(client, echoed));
        CollectionAssert.AreEqual(payload, echoed);
    }

    private static async Task<(TlsStream Client, TlsStream Server, TcpListener Listener)> Open(int maxChunk, int seed)
    {
        using X509Certificate2 certificate = Certificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TlsStream> accept = Accept(listener, certificate, maxChunk, seed + 1);
        var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, port);
        var client = new TlsStream(new ChunkingStream(tcp.GetStream(), new Random(seed), maxChunk));
        await client.AuthenticateAsClientAsync(ClientOptions());
        return (client, await accept, listener);
    }

    private static async Task<TlsStream> Accept(TcpListener listener, X509Certificate2 certificate, int maxChunk, int seed)
    {
        TcpClient accepted = await listener.AcceptTcpClientAsync();
        var stream = new TlsStream(new ChunkingStream(accepted.GetStream(), new Random(seed), maxChunk));
        await stream.AuthenticateAsServerAsync(ServerOptions(certificate));
        return stream;
    }

    private static async Task<int> ReadExact(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }

    private static SslClientAuthenticationOptions ClientOptions() => new()
    {
        TargetHost = "localhost",
        EnabledSslProtocols = SslProtocols.Tls13,
        RemoteCertificateValidationCallback = static (_, _, _, _) => true,
    };

    private static SslServerAuthenticationOptions ServerOptions(X509Certificate2 certificate) => new()
    {
        ServerCertificate = certificate,
        EnabledSslProtocols = SslProtocols.Tls13,
    };

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }

    private sealed class ChunkingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Random _random;
        private readonly int _maxChunk;

        internal ChunkingStream(Stream inner, Random random, int maxChunk)
        {
            _inner = inner;
            _random = random;
            _maxChunk = maxChunk;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            int count = _random.Next(1, Math.Min(buffer.Length, _maxChunk) + 1);
            return _inner.Read(buffer[..count]);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return new ValueTask<int>(0);
            }

            int count = _random.Next(1, Math.Min(buffer.Length, _maxChunk) + 1);
            return _inner.ReadAsync(buffer[..count], cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = _random.Next(1, Math.Min(buffer.Length - offset, _maxChunk) + 1);
                _inner.Write(buffer.Slice(offset, count));
                offset += count;
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = _random.Next(1, Math.Min(buffer.Length - offset, _maxChunk) + 1);
                await _inner.WriteAsync(buffer.Slice(offset, count), cancellationToken).ConfigureAwait(false);
                offset += count;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
        }
    }

    private sealed class CorruptOnceStream : Stream
    {
        private readonly Stream _inner;
        private int _armed;

        internal CorruptOnceStream(Stream inner) => _inner = inner;

        internal void Arm() => _armed = 1;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => WriteCore(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCore(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
        }

        private void WriteCore(ReadOnlySpan<byte> buffer)
        {
            if (Interlocked.CompareExchange(ref _armed, 2, 1) == 1 && buffer.Length > 0)
            {
                byte[] copy = buffer.ToArray();
                copy[copy.Length / 2] ^= 1;
                _inner.Write(copy);
                return;
            }

            _inner.Write(buffer);
        }
    }
}
