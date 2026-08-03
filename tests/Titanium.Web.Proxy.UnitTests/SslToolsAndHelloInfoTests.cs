using System;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.StreamExtended;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class SslToolsAndHelloInfoTests
{
    [TestMethod]
    public void ClientHelloInfo_SslProtocol_MapsKnownVersions()
    {
        var random = new byte[32];
        var session = Array.Empty<byte>();
        var ciphers = new[] { 0x1301 };

        Assert.AreEqual(SslProtocols.Tls12,
            new ClientHelloInfo(3, 3, 3, random, session, ciphers, 0).SslProtocol);
#pragma warning disable SYSLIB0039
        Assert.AreEqual(SslProtocols.Tls11,
            new ClientHelloInfo(3, 3, 2, random, session, ciphers, 0).SslProtocol);
        Assert.AreEqual(SslProtocols.Tls,
            new ClientHelloInfo(3, 3, 1, random, session, ciphers, 0).SslProtocol);
#pragma warning restore SYSLIB0039
#pragma warning disable 618
        Assert.AreEqual(SslProtocols.Ssl3,
            new ClientHelloInfo(3, 3, 0, random, session, ciphers, 0).SslProtocol);
        Assert.AreEqual(SslProtocols.Ssl2,
            new ClientHelloInfo(2, 2, 0, random, session, ciphers, 0).SslProtocol);
#pragma warning restore 618
        Assert.AreEqual(SslProtocols.None,
            new ClientHelloInfo(3, 9, 9, random, session, ciphers, 0).SslProtocol);
    }

    private static readonly int[] ciphers = new[] { 0x1301 };

    [TestMethod]
    public void ClientHelloInfo_TimeAndToString_IncludeExtensions()
    {
        var random = new byte[32];
        // little-endian unix seconds in first 4 bytes (Time property)
        random[0] = 0; random[1] = 0; random[2] = 0; random[3] = 1;
        var hello = new ClientHelloInfo(3, 3, 3, random, new byte[] { 1, 2 }, ciphers, 40)
        {
            CompressionData = new byte[] { 0 },
            Extensions = new System.Collections.Generic.Dictionary<string, SslExtension>
            {
                ["server_name"] = new SslExtension(0, BuildSni("ex.com"), 0)
            }
        };

        Assert.AreNotEqual(DateTime.MinValue, hello.Time);
        var text = hello.ToString();
        StringAssert.Contains(text, "TLS/1.2");
        StringAssert.Contains(text, "server_name");
        StringAssert.Contains(text, "ex.com");
    }

    [TestMethod]
    public void ServerHelloInfo_ToString_IncludesCipherAndCompression()
    {
        var hello = new ServerHelloInfo(3, 3, 3, new byte[32], new byte[] { 9 }, 0x1301, 40)
        {
            CompressionMethod = 0,
            Extensions = new System.Collections.Generic.Dictionary<string, SslExtension>
            {
                ["ALPN"] = new SslExtension(16, BuildAlpn("h2"), 0)
            }
        };

        var text = hello.ToString();
        StringAssert.Contains(text, "TLS/1.2");
        StringAssert.Contains(text, "0x1301");
        StringAssert.Contains(text, "null");
        StringAssert.Contains(text, "ALPN");
    }

    [TestMethod]
    public async Task PeekClientHello_Empty_ReturnsNull()
    {
        var hello = await SslTools.PeekClientHello(new PeekStream(Array.Empty<byte>()), new ArrayPoolBufferPool());
        Assert.IsNull(hello);
    }

    [TestMethod]
    public async Task PeekClientHello_NonHandshake_ReturnsNull()
    {
        var hello = await SslTools.PeekClientHello(new PeekStream(new byte[] { 0x17, 0x03, 0x03 }),
            new ArrayPoolBufferPool());
        Assert.IsNull(hello);
    }

    [TestMethod]
    public async Task PeekClientHello_MinimalTls12_ParsesCiphersAndExtensions()
    {
        var helloBytes = BuildMinimalTls12ClientHello();
        var hello = await SslTools.PeekClientHello(new PeekStream(helloBytes), new ArrayPoolBufferPool());
        Assert.IsNotNull(hello);
        Assert.AreEqual(3, hello!.HandshakeVersion);
        Assert.AreEqual(3, hello.MajorVersion);
        Assert.AreEqual(3, hello.MinorVersion);
        Assert.IsTrue(hello.Ciphers.Length >= 1);
        Assert.IsNotNull(hello.Extensions);
        Assert.IsTrue(hello.Extensions!.ContainsKey("server_name"));
        Assert.AreEqual("example.com", hello.Extensions["server_name"].Data);
    }

    [TestMethod]
    public async Task PeekServerHello_Minimal_Parses()
    {
        var bytes = BuildMinimalTls12ServerHello();
        var hello = await SslTools.PeekServerHello(new PeekStream(bytes), new ArrayPoolBufferPool());
        Assert.IsNotNull(hello);
        Assert.IsTrue(await SslTools.IsServerHello(new PeekStream(bytes), new ArrayPoolBufferPool(),
            CancellationToken.None));
        Assert.AreEqual(0x1301, hello!.CipherSuite);
    }

    [TestMethod]
    public async Task PeekClientHello_Ssl2_ParsesCipherSessionAndRandom()
    {
        var bytes = BuildSsl2ClientHello();

        var hello = await SslTools.PeekClientHello(new PeekStream(bytes), new ArrayPoolBufferPool());

        Assert.IsNotNull(hello);
        Assert.AreEqual(2, hello.HandshakeVersion);
        Assert.AreEqual(2, hello.MajorVersion);
        Assert.AreEqual(0x010080, hello.Ciphers[0]);
        CollectionAssert.AreEqual(new byte[] { 0x44 }, hello.SessionId);
        CollectionAssert.AreEqual(new byte[] { 0xaa, 0xbb }, hello.Random);
    }

    [TestMethod]
    public async Task PeekServerHello_Ssl2_ParsesFixedFields()
    {
        var bytes = BuildSsl2ServerHello();

        var hello = await SslTools.PeekServerHello(new PeekStream(bytes), new ArrayPoolBufferPool());

        Assert.IsNotNull(hello);
        Assert.AreEqual(2, hello.HandshakeVersion);
        Assert.AreEqual(3, hello.MajorVersion);
        Assert.AreEqual(1, hello.MinorVersion);
        Assert.AreEqual(0x002f, hello.CipherSuite);
        Assert.AreEqual(32, hello.Random.Length);
        CollectionAssert.AreEqual(new byte[] { 0x7f }, hello.SessionId);
    }

    [TestMethod]
    public async Task PeekClientHello_Ssl2WrongMessageType_ReturnsNull()
    {
        var bytes = BuildSsl2ClientHello();
        bytes[2] = 0x02;

        var hello = await SslTools.PeekClientHello(new PeekStream(bytes), new ArrayPoolBufferPool());

        Assert.IsNull(hello);
    }

    [TestMethod]
    public async Task PeekServerHello_Ssl2ShortRecord_ReturnsNull()
    {
        var bytes = BuildSsl2ServerHello();
        bytes[0] = 0x80;
        bytes[1] = 37;

        var hello = await SslTools.PeekServerHello(new PeekStream(bytes), new ArrayPoolBufferPool());

        Assert.IsNull(hello);
    }

    [TestMethod]
    public async Task PeekTlsHello_WrongHandshakeTypes_ReturnNull()
    {
        var actualClientHello = BuildMinimalTls12ClientHello();
        var clientBytes = BuildMinimalTls12ClientHello();
        clientBytes[5] = 0x02;
        var serverBytes = BuildMinimalTls12ServerHello();
        serverBytes[5] = 0x01;

        Assert.IsNull(await SslTools.PeekClientHello(new PeekStream(clientBytes), new ArrayPoolBufferPool()));
        Assert.IsNull(await SslTools.PeekServerHello(new PeekStream(serverBytes), new ArrayPoolBufferPool()));
        Assert.IsFalse(await SslTools.IsServerHello(new PeekStream(actualClientHello), new ArrayPoolBufferPool(),
            CancellationToken.None));
    }

    [TestMethod]
    public async Task PeekClientHello_TruncatedTlsRecord_ReturnsNull()
    {
        var bytes = BuildMinimalTls12ClientHello();
        Array.Resize(ref bytes, 20);

        var hello = await SslTools.PeekClientHello(new PeekStream(bytes), new ArrayPoolBufferPool());

        Assert.IsNull(hello);
    }

    private static byte[] BuildSni(string host)
    {
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var entry = new byte[3 + hostBytes.Length];
        entry[0] = 0;
        entry[1] = (byte)(hostBytes.Length >> 8);
        entry[2] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, entry, 3, hostBytes.Length);
        var payload = new byte[2 + entry.Length];
        payload[0] = (byte)(entry.Length >> 8);
        payload[1] = (byte)entry.Length;
        Buffer.BlockCopy(entry, 0, payload, 2, entry.Length);
        return payload;
    }

    private static byte[] BuildSsl2ClientHello()
    {
        // Two-byte record header followed by CLIENT-HELLO, version, lengths, and payload.
        return new byte[]
        {
            0x80, 14,
            0x01,
            0x02, 0x00,
            0x00, 0x03,
            0x00, 0x01,
            0x00, 0x02,
            0x01, 0x00, 0x80,
            0x44,
            0xaa, 0xbb
        };
    }

    private static byte[] BuildSsl2ServerHello()
    {
        var bytes = new byte[40];
        bytes[0] = 0x80;
        bytes[1] = 38;
        bytes[2] = 0x04;
        bytes[3] = 0x03;
        bytes[4] = 0x01;
        for (var i = 0; i < 32; i++) bytes[5 + i] = (byte)i;
        bytes[37] = 0x7f;
        bytes[38] = 0x00;
        bytes[39] = 0x2f;
        return bytes;
    }

    private static byte[] BuildAlpn(string proto)
    {
        var p = Encoding.ASCII.GetBytes(proto);
        var list = new byte[1 + p.Length];
        list[0] = (byte)p.Length;
        Buffer.BlockCopy(p, 0, list, 1, p.Length);
        var payload = new byte[2 + list.Length];
        payload[0] = (byte)(list.Length >> 8);
        payload[1] = (byte)list.Length;
        Buffer.BlockCopy(list, 0, payload, 2, list.Length);
        return payload;
    }

    private static byte[] Ext(ushort type, byte[] data)
    {
        var buf = new byte[4 + data.Length];
        buf[0] = (byte)(type >> 8);
        buf[1] = (byte)type;
        buf[2] = (byte)(data.Length >> 8);
        buf[3] = (byte)data.Length;
        Buffer.BlockCopy(data, 0, buf, 4, data.Length);
        return buf;
    }

    private static byte[] BuildMinimalTls12ClientHello()
    {
        var extensions = Concat(Ext(0, BuildSni("example.com")), Ext(16, BuildAlpn("h2")));
        var extBlock = new byte[2 + extensions.Length];
        extBlock[0] = (byte)(extensions.Length >> 8);
        extBlock[1] = (byte)extensions.Length;
        Buffer.BlockCopy(extensions, 0, extBlock, 2, extensions.Length);

        // handshake body after type+len: ver(2)+random(32)+session(1)+ciphers(2+2)+comp(1+1)+exts
        var body = new System.IO.MemoryStream();
        body.WriteByte(0x03); body.WriteByte(0x03); // version inside hello
        body.Write(new byte[32]); // random
        body.WriteByte(0); // session id len
        body.WriteByte(0); body.WriteByte(2); // cipher len
        body.WriteByte(0x13); body.WriteByte(0x01); // TLS_AES_128_GCM_SHA256
        body.WriteByte(1); // compression methods len
        body.WriteByte(0); // null
        body.Write(extBlock);

        var handshake = body.ToArray();
        var hsLen = handshake.Length;
        var recordPayloadLen = 4 + hsLen; // type + u24 len + body
        var record = new byte[5 + recordPayloadLen];
        record[0] = 0x16;
        record[1] = 0x03; record[2] = 0x03;
        record[3] = (byte)(recordPayloadLen >> 8);
        record[4] = (byte)recordPayloadLen;
        record[5] = 0x01; // ClientHello
        record[6] = (byte)(hsLen >> 16);
        record[7] = (byte)(hsLen >> 8);
        record[8] = (byte)hsLen;
        Buffer.BlockCopy(handshake, 0, record, 9, handshake.Length);
        return record;
    }

    private static byte[] BuildMinimalTls12ServerHello()
    {
        var body = new System.IO.MemoryStream();
        body.WriteByte(0x03); body.WriteByte(0x03);
        body.Write(new byte[32]);
        body.WriteByte(0); // session id
        body.WriteByte(0x13); body.WriteByte(0x01); // cipher
        body.WriteByte(0); // compression
        // no extensions
        var handshake = body.ToArray();
        var hsLen = handshake.Length;
        var recordPayloadLen = 4 + hsLen;
        var record = new byte[5 + recordPayloadLen];
        record[0] = 0x16;
        record[1] = 0x03; record[2] = 0x03;
        record[3] = (byte)(recordPayloadLen >> 8);
        record[4] = (byte)recordPayloadLen;
        record[5] = 0x02; // ServerHello
        record[6] = (byte)(hsLen >> 16);
        record[7] = (byte)(hsLen >> 8);
        record[8] = (byte)hsLen;
        Buffer.BlockCopy(handshake, 0, record, 9, handshake.Length);
        return record;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = 0;
        foreach (var p in parts) len += p.Length;
        var buf = new byte[len];
        var o = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, buf, o, p.Length);
            o += p.Length;
        }

        return buf;
    }

    private sealed class ArrayPoolBufferPool : IBufferPool
    {
        public int BufferSize => 8192;
        public byte[] GetBuffer() => new byte[BufferSize];
        public byte[] GetBuffer(int bufferSize) => new byte[bufferSize];
        public void ReturnBuffer(byte[] buffer) { }
        public void Dispose() { }
    }

    private sealed class PeekStream : IPeekStream
    {
        private readonly byte[] data;
        public PeekStream(byte[] data) => this.data = data;

        public ValueTask<int> PeekBytesAsync(byte[] buffer, int offset, int index, int count,
            CancellationToken cancellationToken = default)
        {
            if (index >= data.Length) return ValueTask.FromResult(0);
            var n = Math.Min(count, data.Length - index);
            Buffer.BlockCopy(data, index, buffer, offset, n);
            return ValueTask.FromResult(n);
        }

        public ValueTask<int> PeekByteAsync(int index, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(index < data.Length ? data[index] : -1);

        public byte PeekByteFromBuffer(int index) => data[index];
    }
}
