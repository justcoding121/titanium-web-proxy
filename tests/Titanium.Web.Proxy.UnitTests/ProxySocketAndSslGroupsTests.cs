using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.ProxySocket;
using Titanium.Web.Proxy.StreamExtended.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxySocketAndSslGroupsTests
{
    [TestMethod]
    [DataRow(0, "Connection succeeded.")]
    [DataRow(1, "General SOCKS server failure.")]
    [DataRow(2, "Connection not allowed by ruleset.")]
    [DataRow(3, "Network unreachable.")]
    [DataRow(4, "Host unreachable.")]
    [DataRow(5, "Connection refused.")]
    [DataRow(6, "TTL expired.")]
    [DataRow(7, "Command not supported.")]
    [DataRow(8, "Address type not supported.")]
    [DataRow(99, "Unspecified SOCKS error.")]
    public void ProxyException_Socks5ToString_MapsCodes(int code, string expected)
    {
        Assert.AreEqual(expected, ProxyException.Socks5ToString(code));
        Assert.AreEqual(expected, new ProxyException(code).Message);
    }

    [TestMethod]
    public void ProxyException_DefaultAndMessageCtors()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(new ProxyException().Message));
        Assert.AreEqual("boom", new ProxyException("boom").Message);
    }

    [TestMethod]
    public void Socks5Handler_GetHostPortBytes_And_GetEndPointBytes_IPv4_IPv6()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var hostMethod = typeof(Socks5Handler).GetMethod("GetHostPortBytes",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var epMethod = typeof(Socks5Handler).GetMethod("GetEndPointBytes",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var hostBuf = new byte[64];
        var hostLen = (int)hostMethod.Invoke(null, ["example.com", 443, hostBuf.AsMemory()])!;
        Assert.AreEqual(7 + "example.com".Length, hostLen);
        Assert.AreEqual(5, hostBuf[0]);
        Assert.AreEqual(3, hostBuf[3]); // domain ATYP
        Assert.AreEqual((byte)"example.com".Length, hostBuf[4]);

        var v4Buf = new byte[16];
        var v4Len = (int)epMethod.Invoke(null, [new IPEndPoint(IPAddress.Parse("1.2.3.4"), 8080), v4Buf.AsMemory()])!;
        Assert.AreEqual(10, v4Len);
        Assert.AreEqual(1, v4Buf[3]); // IPv4 ATYP
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, v4Buf.Skip(4).Take(4).ToArray());

        var v6Buf = new byte[32];
        var v6Len = (int)epMethod.Invoke(null,
            [new IPEndPoint(IPAddress.Parse("::1"), 443), v6Buf.AsMemory()])!;
        Assert.AreEqual(22, v6Len);
        Assert.AreEqual(4, v6Buf[3]); // IPv6 ATYP (regression: must not be ATYP=1)
        CollectionAssert.AreEqual(IPAddress.Parse("::1").GetAddressBytes(), v6Buf.Skip(4).Take(16).ToArray());
    }

    [TestMethod]
    public void Socks5Handler_GetHostPortBytes_ValidationErrors()
    {
        var hostMethod = typeof(Socks5Handler).GetMethod("GetHostPortBytes",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var buf = new byte[16];

        Assert.ThrowsExactly<TargetInvocationException>(() =>
            hostMethod.Invoke(null, [null!, 80, buf.AsMemory()]));
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            hostMethod.Invoke(null, ["host", 0, buf.AsMemory()]));
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            hostMethod.Invoke(null, ["host", 65536, buf.AsMemory()]));
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            hostMethod.Invoke(null, [new string('x', 256), 80, buf.AsMemory()]));
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            hostMethod.Invoke(null, ["host", 80, new byte[4].AsMemory()]));
    }

    [TestMethod]
    public void Socks5Handler_GetEndPointBytes_RejectsUndersizedBuffer_ForIPv6()
    {
        var epMethod = typeof(Socks5Handler).GetMethod("GetEndPointBytes",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            epMethod.Invoke(null, [new IPEndPoint(IPAddress.Parse("::1"), 443), new byte[16].AsMemory()]));
    }

    [TestMethod]
    public void Socks5Handler_ProcessReply_SetsTrailingReadLength_ForAtypVariants()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyEp = (IPEndPoint)listener.LocalEndpoint!;

        var processReply = typeof(Socks5Handler).GetMethod("ProcessReply",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var bufferCountProp = typeof(SocksHandler).GetProperty("BufferCount",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var bufferProp = typeof(SocksHandler).GetProperty("Buffer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var protocolCompleteProp = typeof(SocksHandler).GetProperty("ProtocolComplete",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        HandShakeComplete noop = _ => { };

        void AssertLengthAfterReply(byte atyp, byte[] replyPrefix, int expectedLength)
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sock.Connect(proxyEp);
            using var accepted = listener.AcceptSocket();
            var handler = new Socks5Handler(sock, "", "");
            bufferProp.SetValue(handler, new byte[256]);
            protocolCompleteProp.SetValue(handler, noop);
            var reply = new byte[replyPrefix.Length];
            Buffer.BlockCopy(replyPrefix, 0, reply, 0, replyPrefix.Length);
            reply[3] = atyp;
            processReply.Invoke(handler, new object[] { reply });
            Assert.AreEqual(expectedLength, bufferCountProp.GetValue(handler));
            sock.Close();
            accepted.Close();
        }

        AssertLengthAfterReply(1, new byte[] { 5, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 5);
        AssertLengthAfterReply(3, new byte[] { 5, 0, 0, 0, 5, (byte)'h', (byte)'o', (byte)'s', (byte)'t', (byte)'s' }, 7);
        AssertLengthAfterReply(4, new byte[] { 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 17);
    }

    [TestMethod]
    public void Socks5Handler_ProcessReply_InvalidAtyp_ThrowsProtocolViolation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect((IPEndPoint)listener.LocalEndpoint!);
        using var server = listener.AcceptSocket();

        var handler = new Socks5Handler(client, "", "");
        handler.GetType().GetProperty("Buffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(handler, new byte[32]);
        var processReply = typeof(Socks5Handler).GetMethod("ProcessReply",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reply = new byte[] { 5, 0, 0, 99, 0, 0, 0, 0, 0, 0 };

        var ex = Assert.ThrowsExactly<TargetInvocationException>(() => processReply.Invoke(handler, [reply]));
        Assert.IsInstanceOfType<ProtocolViolationException>(ex.InnerException);
    }

    [TestMethod]
    public void Socks4Handler_GetHostPortBytes_ValidationErrors()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var handler = new Socks4Handler(socket, "user");
        var hostMethod = typeof(Socks4Handler).GetMethod("GetHostPortBytes",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var buf = new byte[64];

        Assert.ThrowsExactly<TargetInvocationException>(() =>
            hostMethod.Invoke(handler, [null!, 80, buf.AsMemory()]));
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            hostMethod.Invoke(handler, ["host", 0, buf.AsMemory()]));
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            hostMethod.Invoke(handler, ["host", 70000, buf.AsMemory()]));
    }

    [TestMethod]
    public void Socks4Handler_GetEndPointBytes_NullRemoteEp_Throws()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var handler = new Socks4Handler(socket, "user");
        var epMethod = typeof(Socks4Handler).GetMethod("GetEndPointBytes",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            epMethod.Invoke(handler, [null!, new byte[32].AsMemory()]));
    }

    [TestMethod]
    public void Socks4Handler_GetHostPortBytes_And_GetEndPointBytes()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var handler = new Socks4Handler(socket, "user");
        var hostMethod = typeof(Socks4Handler).GetMethod("GetHostPortBytes",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var epMethod = typeof(Socks4Handler).GetMethod("GetEndPointBytes",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var hostBuf = new byte[64];
        var hostLen = (int)hostMethod.Invoke(handler, ["host.test", 80, hostBuf.AsMemory()])!;
        Assert.IsTrue(hostLen > 10);
        Assert.AreEqual(4, hostBuf[0]);

        var epBuf = new byte[32];
        var epLen = (int)epMethod.Invoke(handler,
            [new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53), epBuf.AsMemory()])!;
        Assert.IsTrue(epLen >= 9);
        Assert.AreEqual(4, epBuf[0]);
    }

    [TestMethod]
    public void SslExtension_SupportedGroups_AllNamedCurves()
    {
        // Emit every named-curve id the switch knows about so GetSupportedGroup coverage is dense.
        ushort[] ids =
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
            30, 256, 257, 258, 259, 260, 65281, 65282, 0xDEAD
        ];
        var groups = new byte[ids.Length * 2];
        for (var i = 0; i < ids.Length; i++)
        {
            groups[i * 2] = (byte)(ids[i] >> 8);
            groups[i * 2 + 1] = (byte)ids[i];
        }

        var payload = new byte[2 + groups.Length];
        payload[0] = (byte)(groups.Length >> 8);
        payload[1] = (byte)groups.Length;
        Buffer.BlockCopy(groups, 0, payload, 2, groups.Length);

        var data = new SslExtension(10, payload, 0).Data;
        StringAssert.Contains(data, "secp256r1");
        StringAssert.Contains(data, "x25519");
        StringAssert.Contains(data, "ffdhe2048");
        StringAssert.Contains(data, "unknown [0xDEAD]");
        Assert.IsTrue(data.Split(',').Length >= ids.Length - 1);
    }

    [TestMethod]
    public void SslExtension_SignatureAlgorithms_DenseMatrix()
    {
        // Hit many named + legacy branches in GetSignatureAlgorithms.
        ushort[] algs =
        [
            0x0401, 0x0403, 0x0501, 0x0503, 0x0601, 0x0603,
            0x0804, 0x0805, 0x0806, 0x0807, 0x0808, 0x0809, 0x080a, 0x080b,
            0x0201, 0x0203, 0x0301, 0x0303, 0x0101, 0xFFFF
        ];
        var body = new byte[algs.Length * 2];
        for (var i = 0; i < algs.Length; i++)
        {
            body[i * 2] = (byte)(algs[i] >> 8);
            body[i * 2 + 1] = (byte)algs[i];
        }

        var payload = new byte[2 + body.Length];
        payload[0] = (byte)(body.Length >> 8);
        payload[1] = (byte)body.Length;
        Buffer.BlockCopy(body, 0, payload, 2, body.Length);

        var data = new SslExtension(13, payload, 0).Data;
        Assert.IsFalse(string.IsNullOrWhiteSpace(data));
        Assert.IsFalse(data.EndsWith(','));
    }

    [TestMethod]
    public void SslExtension_Name_CoversGreaseAndGoogleIds()
    {
        Assert.AreEqual("Reserved (GREASE)", new SslExtension(0x1a1a, ReadOnlyMemory<byte>.Empty, 0).Name);
        Assert.AreEqual("channel_id", new SslExtension(30032, ReadOnlyMemory<byte>.Empty, 0).Name);
        Assert.AreEqual("next_protocol_negotiation", new SslExtension(13172, ReadOnlyMemory<byte>.Empty, 0).Name);
        Assert.AreEqual("key_share_draft", new SslExtension(40, ReadOnlyMemory<byte>.Empty, 0).Name);
        Assert.AreEqual("key_share", new SslExtension(51, ReadOnlyMemory<byte>.Empty, 0).Name);
        Assert.AreEqual("SessionTicket TLS", new SslExtension(35, ReadOnlyMemory<byte>.Empty, 0).Name);
    }
}
