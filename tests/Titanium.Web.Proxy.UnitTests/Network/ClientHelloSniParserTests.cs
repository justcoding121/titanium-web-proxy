using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Ssl;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ClientHelloSniParserTests
{
    [DataTestMethod]
    [DataRow("api.example.com", SslProtocols.Tls12)]
    [DataRow("a.b.c.example.org", SslProtocols.Tls12 | SslProtocols.Tls13)]
    [DataRow("localhost", SslProtocols.Tls13)]
    [DataRow("x", SslProtocols.Tls12)]
    public async Task RealSslStreamClientHello_YieldsTheTargetHost(string host, SslProtocols protocols)
    {
        var hello = await CaptureClientHelloAsync(host, protocols);

        Assert.IsTrue(ClientHelloSniParser.TryGetServerName(hello, out var name, out var result));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.Found, result);
        Assert.AreEqual(host, name);
    }

    [TestMethod]
    public async Task RealSslStreamClientHello_ToAnIpLiteral_HasNoSni()
    {
        var hello = await CaptureClientHelloAsync("127.0.0.1", SslProtocols.Tls12 | SslProtocols.Tls13);

        Assert.IsFalse(ClientHelloSniParser.TryGetServerName(hello, out var name, out var result));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.NoSni, result);
        Assert.IsNull(name);
    }

    [TestMethod]
    public async Task EveryProperPrefixOfARealClientHello_NeedsMoreData()
    {
        var hello = await CaptureClientHelloAsync("api.example.com", SslProtocols.Tls12 | SslProtocols.Tls13);

        for (var length = 1; length < hello.Length; length++)
        {
            Assert.IsFalse(ClientHelloSniParser.TryGetServerName(hello.AsSpan(0, length), out var name, out var result));
            Assert.AreEqual(ClientHelloSniParser.ParseResult.NeedMoreData, result, $"prefix {length}");
            Assert.IsNull(name);
        }

        Assert.IsTrue(ClientHelloSniParser.TryGetServerName(hello, out _, out _));
    }

    [TestMethod]
    public async Task ABufferWithTrailingBytesAfterTheRecord_StillParses()
    {
        var hello = await CaptureClientHelloAsync("api.example.com", SslProtocols.Tls12);
        var padded = new byte[hello.Length + 64];
        hello.CopyTo(padded, 0);

        Assert.IsTrue(ClientHelloSniParser.TryGetServerName(padded, out var name, out _));
        Assert.AreEqual("api.example.com", name);
    }

    [TestMethod]
    public async Task ReassemblingFragmentedReads_FindsTheNameOnlyOnceTheRecordIsWhole()
    {
        var hello = await CaptureClientHelloAsync("frag.example.com", SslProtocols.Tls12 | SslProtocols.Tls13);
        var buffer = new byte[hello.Length];
        var received = 0;
        ClientHelloSniParser.ParseResult last = default;
        string? found = null;
        foreach (var chunk in new[] { 1, 4, 7, 100, hello.Length })
        {
            var take = Math.Min(chunk, hello.Length - received);
            Array.Copy(hello, received, buffer, received, take);
            received += take;
            ClientHelloSniParser.TryGetServerName(buffer.AsSpan(0, received), out found, out last);
            if (last != ClientHelloSniParser.ParseResult.NeedMoreData)
            {
                break;
            }
        }

        Assert.AreEqual(ClientHelloSniParser.ParseResult.Found, last);
        Assert.AreEqual("frag.example.com", found);
        Assert.AreEqual(hello.Length, received);
    }

    [TestMethod]
    public void Empty_NeedsMoreData()
    {
        Assert.IsFalse(ClientHelloSniParser.TryGetServerName(ReadOnlySpan<byte>.Empty, out _, out var result));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.NeedMoreData, result);
    }

    [DataTestMethod]
    [DataRow("GET / HTTP/1.1\r\nHost: a\r\n\r\n")]
    [DataRow("SSH-2.0-OpenSSH_9.6\r\n")]
    [DataRow("PROXY TCP4 1.2.3.4 5.6.7.8 1 2\r\n")]
    [DataRow("\0\0\0\0")]
    public void PlainText_IsNotTls(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);

        Assert.IsFalse(ClientHelloSniParser.TryGetServerName(bytes, out var name, out var result));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.NotTls, result);
        Assert.IsNull(name);
    }

    [TestMethod]
    public void OneByteOfPlainText_IsAlreadyNotTls()
    {
        Assert.IsFalse(ClientHelloSniParser.TryGetServerName([(byte)'G'], out _, out var result));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.NotTls, result);
    }

    [TestMethod]
    public void NonHandshakeRecords_AreNotTlsClientHellos()
    {
        // Application data, alert, and a ServerHello-typed handshake are not how a client opens a connection.
        foreach (var record in new[]
                 {
                     new byte[] { 0x17, 0x03, 0x03, 0x00, 0x05, 1, 2, 3, 4, 5 },
                     new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 2, 40 },
                     new byte[] { 0x16, 0x03, 0x03, 0x00, 0x05, 0x02, 0, 0, 1, 0 },
                 })
        {
            Assert.IsFalse(ClientHelloSniParser.TryGetServerName(record, out _, out var result));
            Assert.AreEqual(ClientHelloSniParser.ParseResult.NotTls, result);
        }
    }

    [TestMethod]
    public void WrongMajorVersionOrOversizedRecord_IsNotTls()
    {
        Assert.IsFalse(ClientHelloSniParser.TryGetServerName(
            new byte[] { 0x16, 0x04, 0x01, 0x00, 0x05, 0x01, 0, 0, 1, 0 }, out _, out var version));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.NotTls, version);

        // 0x4001 is one byte over the 2^14 plaintext limit.
        Assert.IsFalse(ClientHelloSniParser.TryGetServerName(
            new byte[] { 0x16, 0x03, 0x01, 0x40, 0x01, 0x01 }, out _, out var oversized));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.NotTls, oversized);

        Assert.IsFalse(ClientHelloSniParser.TryGetServerName(
            new byte[] { 0x16, 0x03, 0x01, 0x00, 0x00 }, out _, out var empty));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.NotTls, empty);
    }

    [TestMethod]
    public void HandBuiltHello_WithHostNameAmongOtherExtensions_IsFound()
    {
        var extensions = Concat(
            Extension(0x000b, [1, 0]),
            ServerName("Api.Example.COM"),
            Extension(0x000a, [0, 2, 0, 29]));

        Assert.IsTrue(ClientHelloSniParser.TryGetServerName(Hello(extensions), out var name, out _));
        Assert.AreEqual("Api.Example.COM", name);
    }

    [TestMethod]
    public void HandBuiltHello_SkipsAnUnknownNameTypeBeforeTheHostName()
    {
        var list = Concat(new byte[] { 0x07, 0x00, 0x01, 0xff }, HostNameEntry("later.example.com"));
        var extension = Extension(0x0000, Concat(U16(list.Length), list));

        Assert.IsTrue(ClientHelloSniParser.TryGetServerName(Hello(extension), out var name, out _));
        Assert.AreEqual("later.example.com", name);
    }

    [TestMethod]
    public void HandBuiltHello_TrailingDotIsDropped()
    {
        Assert.IsTrue(ClientHelloSniParser.TryGetServerName(Hello(ServerName("example.com.")), out var name, out _));
        Assert.AreEqual("example.com", name);
    }

    [TestMethod]
    public void HandBuiltHello_WithoutExtensions_HasNoSni()
    {
        Assert.IsFalse(ClientHelloSniParser.TryGetServerName(Hello(null), out var name, out var result));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.NoSni, result);
        Assert.IsNull(name);
    }

    [TestMethod]
    public void HandBuiltHello_WithOtherExtensionsOnly_HasNoSni()
    {
        Assert.IsFalse(ClientHelloSniParser.TryGetServerName(
            Hello(Extension(0x000a, [0, 2, 0, 29])), out _, out var result));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.NoSni, result);
    }

    [TestMethod]
    public void HandBuiltHello_WithNonAsciiOrControlBytesInTheName_HasNoSni()
    {
        foreach (var bad in new[] { "caf\u00e9.example.com", "a b.example.com", "a\u0001b.example.com", string.Empty })
        {
            var entry = Encoding.UTF8.GetBytes(bad);
            var list = Concat(new byte[] { 0x00 }, U16(entry.Length), entry);
            var extension = Extension(0x0000, Concat(U16(list.Length), list));

            Assert.IsFalse(ClientHelloSniParser.TryGetServerName(Hello(extension), out var name, out var result), bad);
            Assert.AreEqual(ClientHelloSniParser.ParseResult.NoSni, result, bad);
            Assert.IsNull(name);
        }
    }

    [TestMethod]
    public void HandBuiltHello_WithLengthsThatOverrunTheRecord_HasNoSniAndNeverThrows()
    {
        var good = Hello(ServerName("example.com"));

        // Lie in every length field, one position at a time. The parser must stay inside the buffer.
        for (var i = 5; i < good.Length; i++)
        {
            foreach (var value in new byte[] { 0x00, 0x01, 0x7f, 0xff })
            {
                var copy = (byte[])good.Clone();
                copy[i] = value;
                var accepted = ClientHelloSniParser.TryGetServerName(copy, out var name, out var result);
                Assert.AreEqual(accepted, result == ClientHelloSniParser.ParseResult.Found);
                Assert.AreEqual(accepted, name is not null);
            }
        }
    }

    [TestMethod]
    public void HandBuiltHello_SplitAcrossTwoRecords_HasNoSni()
    {
        // The handshake header claims more bytes than this record carries.
        var hello = Hello(ServerName("example.com"));
        var body = hello[5..];
        var shortRecord = Concat(new byte[] { 0x16, 0x03, 0x01 }, U16(20), body[..20]);

        Assert.IsFalse(ClientHelloSniParser.TryGetServerName(shortRecord, out _, out var result));
        Assert.AreEqual(ClientHelloSniParser.ParseResult.NoSni, result);
    }

    [TestMethod]
    public void RandomGarbageWithATlsHeader_NeverThrows()
    {
        var random = new Random(12345);
        var buffer = new byte[2048];
        for (var round = 0; round < 2000; round++)
        {
            random.NextBytes(buffer);
            buffer[0] = 0x16;
            buffer[1] = 0x03;
            buffer[2] = 0x01;
            var recordLength = random.Next(0, 2000);
            buffer[3] = (byte)(recordLength >> 8);
            buffer[4] = (byte)recordLength;
            buffer[5] = 0x01;
            ClientHelloSniParser.TryGetServerName(buffer.AsSpan(0, random.Next(0, buffer.Length)), out _, out _);
        }
    }

    private static byte[] Hello(byte[]? extensions)
    {
        var body = new List<byte>();
        body.AddRange(new byte[] { 0x03, 0x03 });
        body.AddRange(new byte[32]);
        body.Add(0); // session id
        body.AddRange(U16(2));
        body.AddRange(new byte[] { 0x13, 0x01 }); // cipher suites
        body.AddRange(new byte[] { 0x01, 0x00 }); // compression
        if (extensions is not null)
        {
            body.AddRange(U16(extensions.Length));
            body.AddRange(extensions);
        }

        var handshake = Concat(new byte[] { 0x01, 0x00 }, U16(body.Count), body.ToArray());
        return Concat(new byte[] { 0x16, 0x03, 0x01 }, U16(handshake.Length), handshake);
    }

    private static byte[] ServerName(string host)
    {
        var list = HostNameEntry(host);
        return Extension(0x0000, Concat(U16(list.Length), list));
    }

    private static byte[] HostNameEntry(string host)
    {
        var name = Encoding.ASCII.GetBytes(host);
        return Concat(new byte[] { 0x00 }, U16(name.Length), name);
    }

    private static byte[] Extension(int type, byte[] data) => Concat(U16(type), U16(data.Length), data);

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (var part in parts)
        {
            result.AddRange(part);
        }

        return result.ToArray();
    }

    /// <summary>Runs the real client handshake against a stream that records what it writes, then never answers.</summary>
    private static async Task<byte[]> CaptureClientHelloAsync(string host, SslProtocols protocols)
    {
        var capture = new CapturingStream();
        using var ssl = new SslStream(capture, leaveInnerStreamOpen: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = protocols }, cts.Token);
        }
        catch (IOException)
        {
            // The capture stream ends the conversation after the first flight, as intended.
        }
        catch (AuthenticationException)
        {
        }

        var bytes = capture.Written.ToArray();
        Assert.IsTrue(bytes.Length > 5, "SslStream wrote no ClientHello.");
        return bytes;
    }

    private sealed class CapturingStream : Stream
    {
        public MemoryStream Written { get; } = new();

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

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("capture complete");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("capture complete");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
