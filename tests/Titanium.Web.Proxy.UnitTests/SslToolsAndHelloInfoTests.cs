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
        // big-endian unix seconds in first 4 bytes (RFC 5246 gmt_unix_time)
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
        Assert.AreEqual(DateTime.UnixEpoch.AddSeconds(1).ToLocalTime(), hello.Time);
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
        Assert.AreEqual(0x010080, hello.CipherSuite);
        Assert.AreEqual(0, hello.Random.Length);
        CollectionAssert.AreEqual(new byte[] { 0x7f, 0x7e }, hello.SessionId);
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
        bytes[1] = 10;

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

    [TestMethod]
    public async Task PeekClientHello_HighExtensionIds_ParseAsUnsigned()
    {
        var helloBytes = BuildTls12ClientHelloWithExtensions(
            Ext(0xFF01, Array.Empty<byte>()),
            Ext(0xFAFA, Array.Empty<byte>()));

        var hello = await SslTools.PeekClientHello(new PeekStream(helloBytes), new ArrayPoolBufferPool());

        Assert.IsNotNull(hello);
        Assert.IsNotNull(hello!.Extensions);
        Assert.IsTrue(hello.Extensions!.ContainsKey("renegotiation_info"));
        Assert.IsTrue(hello.Extensions.ContainsKey("Reserved (GREASE)"));
        Assert.AreEqual(0xFF01, hello.Extensions["renegotiation_info"].Value);
        Assert.AreEqual(0xFAFA, hello.Extensions["Reserved (GREASE)"].Value);
    }

    [TestMethod]
    public async Task PeekClientHello_TruncatedExtensionBlock_DoesNotThrow()
    {
        var helloBytes = BuildMinimalTls12ClientHello();
        // Overwrite extensions length to claim more bytes than remain, leaving a truncated body.
        // Record still has a valid hello prefix (version/random/ciphers/compression).
        var extensionsLengthOffset = FindExtensionsLengthOffset(helloBytes);
        helloBytes[extensionsLengthOffset] = 0x7F;
        helloBytes[extensionsLengthOffset + 1] = 0xFF;

        ClientHelloInfo? hello = null;
        try
        {
            hello = await SslTools.PeekClientHello(new PeekStream(helloBytes), new ArrayPoolBufferPool());
        }
        catch (Exception ex)
        {
            Assert.Fail($"PeekClientHello must not throw on truncated extensions: {ex}");
        }

        // Either null (couldn't buffer claimed length) or a hello with no/partial extensions.
        if (hello != null)
        {
            Assert.AreEqual(3, hello.HandshakeVersion);
            Assert.AreEqual(3, hello.MajorVersion);
        }
    }

    [TestMethod]
    public async Task PeekClientHello_TruncatedExtensionEntry_KeepsPriorExtensions()
    {
        // SNI first, then an extension header that claims more body than remains.
        var sni = Ext(0, BuildSni("example.com"));
        var truncated = new byte[] { 0x00, 0x10, 0x00, 0x20 }; // ALPN, length 32, no body
        var extensions = Concat(sni, truncated);
        var extBlock = new byte[2 + extensions.Length];
        extBlock[0] = (byte)(extensions.Length >> 8);
        extBlock[1] = (byte)extensions.Length;
        Buffer.BlockCopy(extensions, 0, extBlock, 2, extensions.Length);

        var helloBytes = BuildTls12ClientHelloWithExtensionBlock(extBlock);
        ClientHelloInfo? hello = null;
        try
        {
            hello = await SslTools.PeekClientHello(new PeekStream(helloBytes), new ArrayPoolBufferPool());
        }
        catch (Exception ex)
        {
            Assert.Fail($"PeekClientHello must not throw on truncated extension entry: {ex}");
        }

        Assert.IsNotNull(hello);
        Assert.IsNotNull(hello!.Extensions);
        Assert.IsTrue(hello.Extensions!.ContainsKey("server_name"));
        Assert.AreEqual("example.com", hello.Extensions["server_name"].Data);
        Assert.IsFalse(hello.Extensions.ContainsKey("ALPN"));
    }

    [TestMethod]
    public async Task PeekClientHello_OddTlsCipherLength_ReturnsNull()
    {
        var bytes = BuildMinimalTls12ClientHello();
        // cipher length is at: record(5) + hs type/len(4) + ver(2) + random(32) + sessionLen(1) => +0 session
        var cipherLenOffset = 5 + 4 + 2 + 32 + 1;
        bytes[cipherLenOffset] = 0;
        bytes[cipherLenOffset + 1] = 1; // odd length

        Assert.IsNull(await SslTools.PeekClientHello(new PeekStream(bytes), new ArrayPoolBufferPool()));
    }

    [TestMethod]
    public async Task PeekClientHello_Ssl2CipherLengthNotMultipleOf3_ReturnsNull()
    {
        var bytes = BuildSsl2ClientHello();
        // cipher-specs length bytes at offsets 5-6 (after 80 len msg ver)
        bytes[5] = 0;
        bytes[6] = 2; // not divisible by 3

        Assert.IsNull(await SslTools.PeekClientHello(new PeekStream(bytes), new ArrayPoolBufferPool()));
    }

    [TestMethod]
    public async Task PeekServerHello_Ssl2CipherLengthNotMultipleOf3_ReturnsNull()
    {
        var bytes = BuildSsl2ServerHello();
        // cipher-specs-length at bytes 9-10
        bytes[9] = 0;
        bytes[10] = 1;

        Assert.IsNull(await SslTools.PeekServerHello(new PeekStream(bytes), new ArrayPoolBufferPool()));
    }

    [TestMethod]
    public async Task PeekServerHello_Ssl2MultipleCipherSpecs_UsesFirst()
    {
        var bytes = BuildSsl2ServerHello(
            certificate: new byte[] { 0x30 },
            cipherSpecs: new byte[] { 0x01, 0x00, 0x80, 0x02, 0x00, 0x80 },
            connectionId: Array.Empty<byte>());

        var hello = await SslTools.PeekServerHello(new PeekStream(bytes), new ArrayPoolBufferPool());

        Assert.IsNotNull(hello);
        Assert.AreEqual(0x010080, hello!.CipherSuite);
        Assert.AreEqual(0, hello.SessionId.Length);
        Assert.AreEqual(0, hello.Random.Length);
    }

    [TestMethod]
    public async Task PeekServerHello_Ssl2WrongMessageType_ReturnsNull()
    {
        var bytes = BuildSsl2ServerHello();
        bytes[2] = 0x01; // CLIENT-HELLO instead of SERVER-HELLO

        Assert.IsNull(await SslTools.PeekServerHello(new PeekStream(bytes), new ArrayPoolBufferPool()));
    }

    [TestMethod]
    public async Task PeekClientHello_WithSupportedVersions_Tls13_MapsSslProtocol()
    {
        var versions = new byte[] { 4, 0x03, 0x04, 0x03, 0x03 }; // Tls1.3, Tls1.2
        var helloBytes = BuildTls12ClientHelloWithExtensions(Ext(43, versions));

        var hello = await SslTools.PeekClientHello(new PeekStream(helloBytes), new ArrayPoolBufferPool());

        Assert.IsNotNull(hello);
        Assert.AreEqual(SslProtocols.Tls12 | SslProtocols.Tls13, hello!.SslProtocol);
    }

    [TestMethod]
    public void ServerHelloInfo_Time_UsesBigEndianUnixSeconds()
    {
        var random = new byte[32];
        random[0] = 0; random[1] = 0; random[2] = 0; random[3] = 2;
        var hello = new ServerHelloInfo(3, 3, 3, random, Array.Empty<byte>(), 0x1301, 40)
        {
            CompressionMethod = 0,
            ExtensionsStartPosition = 12
        };

        Assert.AreEqual(DateTime.UnixEpoch.AddSeconds(2).ToLocalTime(), hello.Time);
        Assert.AreEqual(12, hello.ExtensionsStartPosition);
        StringAssert.Contains(hello.ToString(), "TLS/1.2");
    }

    [TestMethod]
    public async Task PeekServerHello_WithExtensions_ParsesAlpn()
    {
        var body = new System.IO.MemoryStream();
        body.WriteByte(0x03); body.WriteByte(0x03);
        body.Write(new byte[32]);
        body.WriteByte(0);
        body.WriteByte(0x13); body.WriteByte(0x01);
        body.WriteByte(0);
        var alpn = Ext(16, BuildAlpn("h2"));
        var extBlock = new byte[2 + alpn.Length];
        extBlock[0] = (byte)(alpn.Length >> 8);
        extBlock[1] = (byte)alpn.Length;
        Buffer.BlockCopy(alpn, 0, extBlock, 2, alpn.Length);
        body.Write(extBlock);

        var handshake = body.ToArray();
        var hsLen = handshake.Length;
        var recordPayloadLen = 4 + hsLen;
        var record = new byte[5 + recordPayloadLen];
        record[0] = 0x16;
        record[1] = 0x03; record[2] = 0x03;
        record[3] = (byte)(recordPayloadLen >> 8);
        record[4] = (byte)recordPayloadLen;
        record[5] = 0x02;
        record[6] = (byte)(hsLen >> 16);
        record[7] = (byte)(hsLen >> 8);
        record[8] = (byte)hsLen;
        Buffer.BlockCopy(handshake, 0, record, 9, handshake.Length);

        var hello = await SslTools.PeekServerHello(new PeekStream(record), new ArrayPoolBufferPool());
        Assert.IsNotNull(hello);
        Assert.IsNotNull(hello!.Extensions);
        Assert.IsTrue(hello.Extensions!.ContainsKey("ALPN"));
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

    private static byte[] BuildSsl2ServerHello(
        byte[]? certificate = null,
        byte[]? cipherSpecs = null,
        byte[]? connectionId = null)
    {
        // SSL 2 SERVER-HELLO: hit, cert-type, version, cert-len, cipher-len, conn-id-len,
        // then certificate, 3-byte cipher-specs, connection-id.
        certificate ??= new byte[] { 0x30, 0x01 }; // placeholder DER
        cipherSpecs ??= new byte[] { 0x01, 0x00, 0x80 }; // SSL_CK_RC4_128_WITH_MD5
        connectionId ??= new byte[] { 0x7f, 0x7e };

        var recordLength = 1 + 1 + 1 + 2 + 2 + 2 + 2 + certificate.Length + cipherSpecs.Length + connectionId.Length;
        var bytes = new byte[2 + recordLength];
        bytes[0] = 0x80;
        bytes[1] = (byte)recordLength;
        bytes[2] = 0x04; // SERVER-HELLO
        bytes[3] = 0x00; // SESSION-ID-HIT
        bytes[4] = 0x01; // CERTIFICATE-TYPE (X.509)
        bytes[5] = 0x03; // VERSION major
        bytes[6] = 0x01; // VERSION minor
        bytes[7] = (byte)(certificate.Length >> 8);
        bytes[8] = (byte)certificate.Length;
        bytes[9] = (byte)(cipherSpecs.Length >> 8);
        bytes[10] = (byte)cipherSpecs.Length;
        bytes[11] = (byte)(connectionId.Length >> 8);
        bytes[12] = (byte)connectionId.Length;
        var o = 13;
        Buffer.BlockCopy(certificate, 0, bytes, o, certificate.Length);
        o += certificate.Length;
        Buffer.BlockCopy(cipherSpecs, 0, bytes, o, cipherSpecs.Length);
        o += cipherSpecs.Length;
        Buffer.BlockCopy(connectionId, 0, bytes, o, connectionId.Length);
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
        return BuildTls12ClientHelloWithExtensions(
            Ext(0, BuildSni("example.com")),
            Ext(16, BuildAlpn("h2")));
    }

    private static byte[] BuildTls12ClientHelloWithExtensions(params byte[][] extensionParts)
    {
        var extensions = Concat(extensionParts);
        var extBlock = new byte[2 + extensions.Length];
        extBlock[0] = (byte)(extensions.Length >> 8);
        extBlock[1] = (byte)extensions.Length;
        Buffer.BlockCopy(extensions, 0, extBlock, 2, extensions.Length);
        return BuildTls12ClientHelloWithExtensionBlock(extBlock);
    }

    private static byte[] BuildTls12ClientHelloWithExtensionBlock(byte[] extBlock)
    {
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

    private static int FindExtensionsLengthOffset(byte[] record)
    {
        // After record header (5) + hs type/len (4) + ver(2) + random(32) + sessionLen(1) +
        // session(0) + cipherLen(2) + cipher(2) + compLen(1) + comp(1) => extensions length at 5+4+2+32+1+2+2+1+1
        return 5 + 4 + 2 + 32 + 1 + 2 + 2 + 1 + 1;
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
