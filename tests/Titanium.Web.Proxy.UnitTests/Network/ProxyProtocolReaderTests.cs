using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxyProtocolReaderTests
{
    private static readonly byte[] V2Signature = [0x0d, 0x0a, 0x0d, 0x0a, 0x00, 0x0d, 0x0a, 0x51, 0x55, 0x49, 0x54, 0x0a];

    private static byte[] V2(byte family, byte[] payload)
    {
        var bytes = new byte[16 + payload.Length];
        V2Signature.CopyTo(bytes, 0);
        bytes[12] = 0x21;
        bytes[13] = family;
        bytes[14] = (byte)(payload.Length >> 8);
        bytes[15] = (byte)payload.Length;
        payload.CopyTo(bytes, 16);
        return bytes;
    }

    private static byte[] V2Ipv4(string source, int port)
    {
        var payload = new byte[12];
        IPAddress.Parse(source).GetAddressBytes().CopyTo(payload, 0);
        IPAddress.Parse("10.0.0.2").GetAddressBytes().CopyTo(payload, 4);
        payload[8] = (byte)(port >> 8);
        payload[9] = (byte)port;
        payload[10] = 0x01;
        payload[11] = 0xbb;
        return V2(0x11, payload);
    }

    [TestMethod]
    public void V1_Tcp6_ReadsTheClientAddress()
    {
        var bytes = Encoding.ASCII.GetBytes("PROXY TCP6 2001:db8::1 2001:db8::2 4000 443\r\n");
        Assert.IsTrue(ProxyProtocolReader.TryParse(bytes, out var client, out var consumed));
        Assert.AreEqual(bytes.Length, consumed);
        Assert.AreEqual(IPAddress.Parse("2001:db8::1"), client!.Address);
        Assert.AreEqual(4000, client.Port);
    }

    [TestMethod]
    public void V1_Unknown_ConsumesTheLineWithoutAClient()
    {
        var bytes = Encoding.ASCII.GetBytes("PROXY UNKNOWN\r\nrest");
        Assert.IsTrue(ProxyProtocolReader.TryParse(bytes, out var client, out var consumed));
        Assert.IsNull(client);
        Assert.AreEqual(15, consumed);
    }

    [DataTestMethod]
    [DataRow("PROXY TCP4 203.0.113.1 203.0.113.2 12345 443")]
    [DataRow("PROXY TCP4 not-an-ip 203.0.113.2 1 2\r\n")]
    [DataRow("PROXY TCP4 203.0.113.1 203.0.113.2 port 2\r\n")]
    [DataRow("PROXY\r\n")]
    public void V1_Malformed_IsRejected(string line)
    {
        Assert.IsFalse(ProxyProtocolReader.TryParse(Encoding.ASCII.GetBytes(line), out var client, out _));
        Assert.IsNull(client);
    }

    [TestMethod]
    public void NotAPreamble_IsRejected()
    {
        Assert.IsFalse(ProxyProtocolReader.TryParse("GET / HTTP/1.1\r\n"u8, out _, out var consumed));
        Assert.AreEqual(0, consumed);
    }

    [TestMethod]
    public void V2_Ipv4_ReadsTheClientAddress()
    {
        var bytes = V2Ipv4("198.51.100.7", 40000);
        Assert.IsTrue(ProxyProtocolReader.TryParse(bytes, out var client, out var consumed));
        Assert.AreEqual(bytes.Length, consumed);
        Assert.AreEqual(IPAddress.Parse("198.51.100.7"), client!.Address);
        Assert.AreEqual(40000, client.Port);
    }

    [TestMethod]
    public void V2_Ipv6_ReadsTheClientAddress()
    {
        var payload = new byte[36];
        IPAddress.Parse("2001:db8::9").GetAddressBytes().CopyTo(payload, 0);
        IPAddress.Parse("2001:db8::a").GetAddressBytes().CopyTo(payload, 16);
        payload[32] = 0x9c;
        payload[33] = 0x40;
        var bytes = V2(0x21, payload);
        Assert.IsTrue(ProxyProtocolReader.TryParse(bytes, out var client, out var consumed));
        Assert.AreEqual(bytes.Length, consumed);
        Assert.AreEqual(IPAddress.Parse("2001:db8::9"), client!.Address);
        Assert.AreEqual(40000, client.Port);
    }

    [TestMethod]
    public void V2_Local_HasNoClient()
    {
        var bytes = V2(0x00, []);
        Assert.IsTrue(ProxyProtocolReader.TryParse(bytes, out var client, out var consumed));
        Assert.IsNull(client);
        Assert.AreEqual(16, consumed);
    }

    [TestMethod]
    public void V2_Truncated_IsRejected()
    {
        var bytes = V2Ipv4("198.51.100.7", 1);
        Assert.IsFalse(ProxyProtocolReader.TryParse(bytes.AsSpan(0, bytes.Length - 3), out _, out _));
    }

    private static async Task<(Socket Server, Socket Client, TcpListener Listener)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = listener.AcceptSocketAsync();
        var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        return (await accepted, client, listener);
    }

    [TestMethod]
    public async Task TryReadAsync_V1_LeavesTheApplicationBytesInTheSocket()
    {
        var (server, client, listener) = await PairAsync();
        using (server)
        using (client)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // The first request bytes arrive in the same segment as the preamble.
            await client.SendAsync(Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.9 10.0.0.2 5555 443\r\nGET / HTTP/1.1\r\n"), cts.Token);
            Assert.IsTrue(await ProxyProtocolReader.TryReadAsync(server, cts.Token));
            Assert.IsTrue(ProxyProtocolForwarded.TryTake(server, out var forwarded));
            Assert.AreEqual(IPAddress.Parse("203.0.113.9"), forwarded!.Address);
            Assert.AreEqual(5555, forwarded.Port);
            var rest = new byte[16];
            var read = 0;
            while (read < rest.Length)
            {
                read += await server.ReceiveAsync(rest.AsMemory(read), cts.Token);
            }

            Assert.AreEqual("GET / HTTP/1.1\r\n", Encoding.ASCII.GetString(rest));
        }

        listener.Stop();
    }

    [TestMethod]
    public async Task TryReadAsync_V2_LeavesTheApplicationBytesInTheSocket()
    {
        var (server, client, listener) = await PairAsync();
        using (server)
        using (client)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var preamble = V2Ipv4("198.51.100.7", 40001);
            var wire = new byte[preamble.Length + 3];
            preamble.CopyTo(wire, 0);
            "abc"u8.CopyTo(wire.AsSpan(preamble.Length));
            await client.SendAsync(wire, cts.Token);
            Assert.IsTrue(await ProxyProtocolReader.TryReadAsync(server, cts.Token));
            Assert.IsTrue(ProxyProtocolForwarded.TryTake(server, out var forwarded));
            Assert.AreEqual(40001, forwarded!.Port);
            var rest = new byte[3];
            var read = 0;
            while (read < rest.Length)
            {
                read += await server.ReceiveAsync(rest.AsMemory(read), cts.Token);
            }

            Assert.AreEqual("abc", Encoding.ASCII.GetString(rest));
            Assert.IsFalse(ProxyProtocolForwarded.TryTake(server, out _), "the address is handed over once");
        }

        listener.Stop();
    }

    [TestMethod]
    public async Task TryReadAsync_Unknown_AcceptsWithoutRemembering()
    {
        var (server, client, listener) = await PairAsync();
        using (server)
        using (client)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.SendAsync(Encoding.ASCII.GetBytes("PROXY UNKNOWN\r\n"), cts.Token);
            Assert.IsTrue(await ProxyProtocolReader.TryReadAsync(server, cts.Token));
            Assert.IsFalse(ProxyProtocolForwarded.TryTake(server, out _));
        }

        listener.Stop();
    }

    [DataTestMethod]
    [DataRow("GET / HTTP/1.1\r\nHost: x\r\n\r\n")]
    [DataRow("PROXY TCP4 bad\r\n")]
    public async Task TryReadAsync_NotAPreamble_IsRejected(string wire)
    {
        var (server, client, listener) = await PairAsync();
        using (server)
        using (client)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.SendAsync(Encoding.ASCII.GetBytes(wire), cts.Token);
            Assert.IsFalse(await ProxyProtocolReader.TryReadAsync(server, cts.Token));
        }

        listener.Stop();
    }

    [TestMethod]
    public async Task TryReadAsync_ClosedBeforeThePreambleEnds_IsRejected()
    {
        var (server, client, listener) = await PairAsync();
        using (server)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.SendAsync(Encoding.ASCII.GetBytes("PROXY TCP4 1.2"), cts.Token);
            client.Dispose();
            Assert.IsFalse(await ProxyProtocolReader.TryReadAsync(server, cts.Token));
        }

        listener.Stop();
    }
}
