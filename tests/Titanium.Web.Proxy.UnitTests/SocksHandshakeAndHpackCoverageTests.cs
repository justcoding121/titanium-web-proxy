using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Models;
using HpackDecoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;
using ProxySock = Titanium.Web.Proxy.ProxySocket.ProxySocket;
using ProxyTypes = Titanium.Web.Proxy.ProxySocket.ProxyTypes;
using ProxyException = Titanium.Web.Proxy.ProxySocket.ProxyException;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class SocksHandshakeAndHpackCoverageTests
{
    [TestMethod]
    public async Task ProxySocket_Socks5_AuthNone_ConnectsThroughFakePeer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyEp = (IPEndPoint)listener.LocalEndpoint;

        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[32];
            Assert.IsTrue(await ReadExactAsync(stream, buf, 0, 4)); // 05 02 00 02
            Assert.AreEqual(5, buf[0]);
            await stream.WriteAsync(new byte[] { 0x05, 0x00 }); // no-auth
            Assert.IsTrue(await ReadExactAsync(stream, buf, 0, 10)); // CONNECT IPv4
            Assert.AreEqual(5, buf[0]);
            Assert.AreEqual(1, buf[1]); // CONNECT
            Assert.AreEqual(1, buf[3]); // ATYP IPv4
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
        });

        using var sock = new ProxySock(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ProxyType = ProxyTypes.Socks5,
            ProxyEndPoint = proxyEp,
            ProxyUser = "",
            ProxyPass = ""
        };
        var ar = sock.BeginConnect(IPAddress.Parse("1.2.3.4"), 80, null, null);
        sock.EndConnect(ar);
        await peer;
    }

    [TestMethod]
    public async Task ProxySocket_Socks5_UserPass_ThenDomainConnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyEp = (IPEndPoint)listener.LocalEndpoint;

        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[64];
            Assert.IsTrue(await ReadExactAsync(stream, buf, 0, 4));
            await stream.WriteAsync(new byte[] { 0x05, 0x02 }); // username/password
            // RFC1929: ver=1, ulen, user, plen, pass
            Assert.IsTrue(await ReadExactAsync(stream, buf, 0, 2));
            Assert.AreEqual(1, buf[0]);
            var ulen = buf[1];
            Assert.IsTrue(await ReadExactAsync(stream, buf, 0, ulen + 1));
            var plen = buf[ulen];
            Assert.IsTrue(await ReadExactAsync(stream, buf, 0, plen));
            await stream.WriteAsync(new byte[] { 0x01, 0x00 }); // auth ok
            // CONNECT domain
            Assert.IsTrue(await ReadExactAsync(stream, buf, 0, 5));
            Assert.AreEqual(3, buf[3]); // domain
            var hlen = buf[4];
            Assert.IsTrue(await ReadExactAsync(stream, buf, 0, hlen + 2));
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
        });

        using var sock = new ProxySock(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ProxyType = ProxyTypes.Socks5,
            ProxyEndPoint = proxyEp,
            ProxyUser = "alice",
            ProxyPass = "secret"
        };
        var ar = sock.BeginConnect("example.com", 443, null, null);
        sock.EndConnect(ar);
        await peer;
    }

    [TestMethod]
    public async Task ProxySocket_Socks5_UnsupportedAuth_Throws()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyEp = (IPEndPoint)listener.LocalEndpoint;

        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[8];
            await ReadExactAsync(stream, buf, 0, 4);
            await stream.WriteAsync(new byte[] { 0x05, 0xFF });
        });

        using var sock = new ProxySock(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ProxyType = ProxyTypes.Socks5,
            ProxyEndPoint = proxyEp
        };
        var ar = sock.BeginConnect(IPAddress.Loopback, 9, null, null);
        try
        {
            sock.EndConnect(ar);
            Assert.Fail("expected handshake failure");
        }
        catch (Exception ex) when (ex is ProxyException or SocketException)
        {
            // Unsupported method may surface as ProxyException or SocketException depending on peer close timing.
        }

        await peer;
    }

    [TestMethod]
    public async Task ProxySocket_Socks4_Reply90_Succeeds_Reply91_Fails()
    {
        using var okListener = new TcpListener(IPAddress.Loopback, 0);
        okListener.Start();
        var okEp = (IPEndPoint)okListener.LocalEndpoint;
        var okPeer = Task.Run(async () =>
        {
            using var client = await okListener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[32];
            // Read until userid NUL
            var n = 0;
            while (n < buf.Length)
            {
                var r = await stream.ReadAsync(buf.AsMemory(n, 1));
                if (r == 0) break;
                n++;
                if (n >= 9 && buf[n - 1] == 0) break;
            }

            await stream.WriteAsync(new byte[] { 0x00, 0x5A, 0, 0, 0, 0, 0, 0 });
        });

        using (var sock = new ProxySock(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ProxyType = ProxyTypes.Socks4,
            ProxyEndPoint = okEp,
            ProxyUser = "user"
        })
        {
            var ar = sock.BeginConnect(IPAddress.Parse("8.8.8.8"), 53, null, null);
            sock.EndConnect(ar);
        }

        await okPeer;

        using var failListener = new TcpListener(IPAddress.Loopback, 0);
        failListener.Start();
        var failEp = (IPEndPoint)failListener.LocalEndpoint;
        var failPeer = Task.Run(async () =>
        {
            using var client = await failListener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[32];
            var n = 0;
            while (n < buf.Length)
            {
                var r = await stream.ReadAsync(buf.AsMemory(n, 1));
                if (r == 0) break;
                n++;
                if (n >= 9 && buf[n - 1] == 0) break;
            }

            await stream.WriteAsync(new byte[] { 0x00, 0x5B, 0, 0, 0, 0, 0, 0 });
        });

        using (var sock = new ProxySock(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ProxyType = ProxyTypes.Socks4,
            ProxyEndPoint = failEp,
            ProxyUser = "user"
        })
        {
            var ar = sock.BeginConnect(IPAddress.Parse("1.1.1.1"), 80, null, null);
            var ex = Assert.ThrowsException<ProxyException>(() => sock.EndConnect(ar));
            StringAssert.Contains(ex.Message, "Negotiation failed");
        }

        await failPeer;
    }

    [TestMethod]
    public void ProxySocket_BeginConnect_ValidationAndDirectPath()
    {
        using var sock = new ProxySock(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Assert.ThrowsException<ArgumentNullException>(() => sock.BeginConnect((string)null!, 80, null, null));
        Assert.ThrowsException<ArgumentException>(() => sock.BeginConnect("h", 0, null, null));
        Assert.ThrowsException<ArgumentException>(() => sock.BeginConnect("h", 65536, null, null));
        Assert.ThrowsException<ArgumentNullException>(() => sock.EndConnect(null!));

        sock.ProxyUser = "u";
        sock.ProxyPass = "p";
        Assert.ThrowsException<ArgumentNullException>(() => sock.ProxyUser = null!);
        Assert.ThrowsException<ArgumentNullException>(() => sock.ProxyPass = null!);
    }

    [TestMethod]
    public void HpackDecoder_LiteralIncrementalNeverIndexed_AndIllegalIndex()
    {
        var listener = new RecordingHeaderListener();
        var decoder = new HpackDecoder(8192, 4096);
        // incremental indexing new name: 0x40 + name + value
        var incremental = new byte[]
        {
            0x40, 0x03, (byte)'f', (byte)'o', (byte)'o', 0x03, (byte)'b', (byte)'a', (byte)'r'
        };
        using (var stream = new MemoryStream(incremental))
        using (var reader = new BinaryReader(stream))
        {
            decoder.Decode(reader, listener);
            decoder.EndHeaderBlock();
        }

        Assert.AreEqual(1, listener.Headers.Count);
        Assert.AreEqual("foo", listener.Headers[0].Name);
        Assert.IsFalse(listener.Headers[0].Sensitive);

        listener.Headers.Clear();
        var never = new byte[]
        {
            0x10, 0x03, (byte)'x', (byte)'y', (byte)'z', 0x03, (byte)'a', (byte)'b', (byte)'c'
        };
        using (var stream = new MemoryStream(never))
        using (var reader = new BinaryReader(stream))
        {
            decoder.Decode(reader, listener);
            decoder.EndHeaderBlock();
        }

        Assert.AreEqual(1, listener.Headers.Count);
        Assert.AreEqual("xyz", listener.Headers[0].Name);
        Assert.IsTrue(listener.Headers[0].Sensitive);

        // illegal dynamic index on empty/almost-empty table
        var bad = new HpackDecoder(8192, 4096);
        using (var stream = new MemoryStream(new byte[] { 0xC0 })) // indexed 64
        using (var reader = new BinaryReader(stream))
        {
            Assert.ThrowsException<IOException>(() => bad.Decode(reader, new RecordingHeaderListener()));
        }
    }

    [TestMethod]
    public void HpackDecoder_EmptyValue_DtsuUle128_AndTruncation()
    {
        var listener = new RecordingHeaderListener();
        var decoder = new HpackDecoder(8192, 4096);
        var emptyValue = new byte[] { 0x00, 0x03, (byte)'f', (byte)'o', (byte)'o', 0x00 };
        using (var stream = new MemoryStream(emptyValue))
        using (var reader = new BinaryReader(stream))
        {
            decoder.Decode(reader, listener);
            Assert.IsFalse(decoder.EndHeaderBlock());
        }

        Assert.AreEqual(1, listener.Headers.Count);
        Assert.AreEqual("", listener.Headers[0].Value);

        var dtsu = new HpackDecoder(8192, 4096);
        // 0x3F 0x01 => DTSU size 32 (31+1)
        using (var stream = new MemoryStream(new byte[] { 0x3F, 0x01, 0x82 }))
        using (var reader = new BinaryReader(stream))
        {
            dtsu.Decode(reader, new RecordingHeaderListener());
            dtsu.EndHeaderBlock();
        }

        Assert.AreEqual(32, dtsu.GetMaxHeaderTableSize());

        var tiny = new HpackDecoder(maxHeaderSize: 1, maxHeaderTableSize: 4096);
        using (var stream = new MemoryStream(new byte[]
               { 0x00, 0x03, (byte)'a', (byte)'b', (byte)'c', 0x03, (byte)'d', (byte)'e', (byte)'f' }))
        using (var reader = new BinaryReader(stream))
        {
            tiny.Decode(reader, new RecordingHeaderListener());
            Assert.IsTrue(tiny.EndHeaderBlock());
        }

        var capped = new HpackDecoder(8192, 4096);
        capped.SetMaxHeaderTableSize(100);
        // DTSU above max: 0x3F then large ULE
        using (var stream = new MemoryStream(new byte[] { 0x3F, 0x60 }))
        using (var reader = new BinaryReader(stream))
        {
            Assert.ThrowsException<IOException>(() => capped.Decode(reader, new RecordingHeaderListener()));
        }
    }

    [TestMethod]
    [DataRow(0, "server_name")]
    [DataRow(1, "max_fragment_length")]
    [DataRow(5, "status_request")]
    [DataRow(10, "supported_groups")]
    [DataRow(11, "ec_point_formats")]
    [DataRow(13, "signature_algorithms")]
    [DataRow(16, "ALPN")]
    [DataRow(21, "padding")]
    [DataRow(35, "SessionTicket TLS")]
    [DataRow(40, "key_share")]
    [DataRow(43, "supported_versions")]
    [DataRow(45, "psk_key_exchange_modes")]
    [DataRow(47, "certificate_authorities")]
    [DataRow(49, "post_handshake_auth")]
    [DataRow(0x0A0A, "Reserved (GREASE)")]
    [DataRow(13172, "next_protocol_negotiation")]
    [DataRow(30032, "channel_id")]
    [DataRow(65281, "renegotiation_info")]
    [DataRow(65282, "Draft version of TLS 1.3")]
    public void SslExtension_Name_MapsKnownIds(int id, string expected)
    {
        Assert.AreEqual(expected, new SslExtension(id, Array.Empty<byte>(), 0).Name);
    }

    [TestMethod]
    public void SslExtension_StatusRequest_NonOcsp_AndEcPointFormats()
    {
        var status = new SslExtension(5, new byte[] { 2, 0, 0, 0, 0 }, 0).Data;
        Assert.IsFalse(status.StartsWith("OCSP", StringComparison.Ordinal));

        var ec = new SslExtension(11, new byte[] { 6, 0, 0, 1, 0, 2, 0 }, 0).Data;
        StringAssert.Contains(ec, "uncompressed");
        StringAssert.Contains(ec, "ansiX962_compressed_prime");
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read));
            if (n == 0) return false;
            read += n;
        }

        return true;
    }

    private sealed class RecordingHeaderListener : IHeaderListener
    {
        internal List<(string Name, string Value, bool Sensitive)> Headers { get; } = new();

        public void AddHeader(ByteString name, ByteString value, bool sensitive)
        {
            Headers.Add((name.ToString(), value.ToString(), sensitive));
        }
    }
}
