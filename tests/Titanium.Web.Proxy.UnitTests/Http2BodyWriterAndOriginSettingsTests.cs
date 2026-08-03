using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using HpackDecoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2BodyWriterAndOriginSettingsTests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public async Task Http2BodyStreamWriter_WritesSplitFramesAndCompleteEndStream()
    {
        var writerType = typeof(Http2Helper).GetNestedType("Http2BodyStreamWriter", BindingFlags.NonPublic)!;
        using var ms = new MemoryStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        var writer = Activator.CreateInstance(writerType, PrivateInstance, binder: null,
            args: [1, ms, writeLock, flow, CancellationToken.None], culture: null)!;

        Assert.IsFalse((bool)writerType.GetProperty("CanRead")!.GetValue(writer)!);
        Assert.IsFalse((bool)writerType.GetProperty("CanSeek")!.GetValue(writer)!);
        Assert.IsTrue((bool)writerType.GetProperty("CanWrite")!.GetValue(writer)!);
        Assert.IsInstanceOfType(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                _ = writerType.GetProperty("Length")!.GetValue(writer)).InnerException,
            typeof(NotSupportedException));
        Assert.IsInstanceOfType(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                _ = writerType.GetProperty("Position")!.GetValue(writer)).InnerException,
            typeof(NotSupportedException));
        Assert.IsInstanceOfType(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                writerType.GetProperty("Position")!.SetValue(writer, 0L)).InnerException,
            typeof(NotSupportedException));
        Assert.IsInstanceOfType(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                writerType.GetMethod("Read", [typeof(byte[]), typeof(int), typeof(int)])!
                    .Invoke(writer, [new byte[1], 0, 1])).InnerException,
            typeof(NotSupportedException));
        Assert.IsInstanceOfType(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                writerType.GetMethod("Seek")!.Invoke(writer, [0L, SeekOrigin.Begin])).InnerException,
            typeof(NotSupportedException));
        Assert.IsInstanceOfType(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                writerType.GetMethod("SetLength")!.Invoke(writer, [0L])).InnerException,
            typeof(NotSupportedException));
        Assert.IsInstanceOfType(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                writerType.GetMethod("Write", [typeof(byte[]), typeof(int), typeof(int)])!
                    .Invoke(writer, [new byte[1], 0, 1])).InnerException,
            typeof(NotSupportedException));

        writerType.GetMethod("Flush")!.Invoke(writer, null);
        await (Task)writerType.GetMethod("FlushAsync", [typeof(CancellationToken)])!
            .Invoke(writer, [CancellationToken.None])!;

        // Payload larger than SafeMaxFrameSize (16384) to force a split.
        var payload = new byte[20000];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        var writeAsync = writerType.GetMethod("WriteAsync",
            [typeof(byte[]), typeof(int), typeof(int), typeof(CancellationToken)])!;
        await (Task)writeAsync.Invoke(writer, [payload, 0, payload.Length, CancellationToken.None])!;

        // Empty write is a no-op
        await (Task)writeAsync.Invoke(writer, [Array.Empty<byte>(), 0, 0, CancellationToken.None])!;

        var complete = writerType.GetMethod("CompleteAsync", BindingFlags.Instance | BindingFlags.NonPublic |
                                                            BindingFlags.Public)!;
        await (Task)complete.Invoke(writer, null)!;
        await (Task)complete.Invoke(writer, null)!; // idempotent

        var wire = ms.ToArray();
        Assert.IsTrue(wire.Length > 9 + 16384 + 9);
        Assert.AreEqual((byte)Http2FrameType.Data, wire[3]);
        // Final empty END_STREAM frame
        Assert.AreEqual((byte)Http2FrameType.Data, wire[wire.Length - 6]);
        Assert.AreEqual((byte)Http2FrameFlag.EndStream, wire[wire.Length - 5]);
    }

    [TestMethod]
    public void MyHeaderListener_ResponseDirection_RejectsRequestPseudosAndDupStatus()
    {
        var resp = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: false);
        resp.AddHeader(Bs(":status"), Bs("200"), false);
        resp.AddHeader(Bs(":status"), Bs("204"), false);
        Assert.IsTrue(resp.HasMalformedHeader);
        StringAssert.Contains(resp.MalformedReason, "duplicate");

        foreach (var name in new[] { ":authority", ":scheme", ":path", ":protocol" })
        {
            var listener = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: false);
            listener.AddHeader(Bs(name), Bs("x"), false);
            Assert.IsTrue(listener.HasMalformedHeader, name);
            StringAssert.Contains(listener.MalformedReason, "response");
        }
    }

    [TestMethod]
    public void MyHeaderListener_Request_RejectsDuplicateAuthoritySchemePathProtocol()
    {
        void AssertDup(string name, string value)
        {
            var listener = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
            listener.AddHeader(Bs(name), Bs(value), false);
            listener.AddHeader(Bs(name), Bs(value), false);
            Assert.IsTrue(listener.HasMalformedHeader, name);
            StringAssert.Contains(listener.MalformedReason, "duplicate");
        }

        AssertDup(":authority", "h");
        AssertDup(":scheme", "https");
        AssertDup(":path", "/");
        AssertDup(":protocol", "websocket");
    }

    [TestMethod]
    public void MyHeaderListener_SchemeProperty_MapsHttpAndHttps()
    {
        var http = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        http.AddHeader(Bs(":scheme"), ProxyServer.UriSchemeHttp8, false);
        Assert.AreEqual(ProxyServer.UriSchemeHttp, http.Scheme);

        var https = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        https.AddHeader(Bs(":scheme"), ProxyServer.UriSchemeHttps8, false);
        Assert.AreEqual(ProxyServer.UriSchemeHttps, https.Scheme);

        var other = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        other.AddHeader(Bs(":scheme"), Bs("ftp"), false);
        Assert.AreEqual(string.Empty, other.Scheme);
    }

    [TestMethod]
    public async Task ApplySettings_OutOfRangeMaxFrameSize_FaultsConnection()
    {
        using var origin = await CreateOriginConnectionShellAsync();
        var apply = typeof(Http2OriginConnection).GetMethod("ApplySettings", PrivateInstance)!;

        // SETTINGS_MAX_FRAME_SIZE = 0x5, value = 100 (too small)
        var payload = new byte[] { 0x00, 0x05, 0x00, 0x00, 0x00, 100 };
        apply.Invoke(origin, [payload]);
        Assert.IsFalse(origin.IsUsable);
    }

    [TestMethod]
    public async Task ApplySettings_NegativeInitialWindowSize_FaultsConnection()
    {
        using var origin = await CreateOriginConnectionShellAsync();
        var apply = typeof(Http2OriginConnection).GetMethod("ApplySettings", PrivateInstance)!;

        // SETTINGS_INITIAL_WINDOW_SIZE = 0x4, value with high bit set → negative int
        var payload = new byte[] { 0x00, 0x04, 0x80, 0x00, 0x00, 0x00 };
        apply.Invoke(origin, [payload]);
        Assert.IsFalse(origin.IsUsable);
    }

    [TestMethod]
    public async Task ApplySettings_ValidMaxFrameAndWindow_UpdatesState()
    {
        using var origin = await CreateOriginConnectionShellAsync();
        var apply = typeof(Http2OriginConnection).GetMethod("ApplySettings", PrivateInstance)!;

        var payload = new byte[18];
        // MAX_FRAME_SIZE = 20000
        payload[0] = 0x00;
        payload[1] = 0x05;
        payload[2] = 0x00;
        payload[3] = 0x00;
        payload[4] = 0x4E;
        payload[5] = 0x20; // 20000
        // INITIAL_WINDOW_SIZE = 65535
        payload[6] = 0x00;
        payload[7] = 0x04;
        payload[8] = 0x00;
        payload[9] = 0x00;
        payload[10] = 0xFF;
        payload[11] = 0xFF;
        // MAX_CONCURRENT_STREAMS = 50
        payload[12] = 0x00;
        payload[13] = 0x03;
        payload[14] = 0x00;
        payload[15] = 0x00;
        payload[16] = 0x00;
        payload[17] = 50;

        apply.Invoke(origin, [payload]);
        Assert.IsTrue(origin.IsUsable);

        var settingsField = typeof(Http2OriginConnection).GetField("originSettings", PrivateInstance)!;
        var settings = (Http2Settings)settingsField.GetValue(origin)!;
        Assert.AreEqual(20000, settings.MaxFrameSize);
        Assert.AreEqual(50, settings.MaxConcurrentStreams);
    }

    [TestMethod]
    public void HeaderCollectorListener_ForwardsAllHeadersToCallback()
    {
        var listenerType = typeof(Http2OriginConnection).GetNestedType("HeaderCollectorListener",
            BindingFlags.NonPublic)!;
        var headers = new System.Collections.Generic.List<(string Name, string Value)>();
        var listener = Activator.CreateInstance(listenerType, PrivateInstance, null,
            [(Action<ByteString, ByteString>)((n, v) =>
            {
                headers.Add((Encoding.ASCII.GetString(n.Span), Encoding.ASCII.GetString(v.Span)));
            })], null)!;

        var add = listenerType.GetMethod("AddHeader")!;
        add.Invoke(listener, [Bs(":status"), Bs("100"), false]);
        add.Invoke(listener, [Bs("x-a"), Bs("1"), false]);
        add.Invoke(listener, [Bs("x-b"), Bs("2"), true]);

        Assert.AreEqual(3, headers.Count);
        Assert.AreEqual(":status", headers[0].Name);
        Assert.AreEqual("100", headers[0].Value);
        Assert.AreEqual("x-a", headers[1].Name);
        Assert.AreEqual("x-b", headers[2].Name);
    }

    [TestMethod]
    public void HpackDecoder_HuffmanEncodedLiteral_EmitsHeader()
    {
        using var encoded = new MemoryStream();
        using (var writer = new BinaryWriter(encoded, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((byte)0x00); // literal, no indexing, new name
            WriteHuffmanString(writer, Encoding.ASCII.GetBytes("foo"));
            WriteHuffmanString(writer, Encoding.ASCII.GetBytes("bar"));
        }

        var listener = new RecordingHeaderListener();
        var decoder = new HpackDecoder(8192, 4096);
        encoded.Position = 0;
        using var reader = new BinaryReader(encoded);
        decoder.Decode(reader, listener);
        decoder.EndHeaderBlock();

        Assert.AreEqual(1, listener.Headers.Count);
        Assert.AreEqual("foo", listener.Headers[0].Item1);
        Assert.AreEqual("bar", listener.Headers[0].Item2);
    }

    [TestMethod]
    public void HpackDecoder_TruncatedUle128_ResumesOnSecondDecode()
    {
        // DTSU with size needing continuation: 0x3F then more bytes for size > 31
        // First feed only 0x3F (incomplete ULE128), then complete with 0x01 and a header.
        var decoder = new HpackDecoder(8192, 4096);
        using (var partial = new MemoryStream(new byte[] { 0x3F }))
        using (var reader = new BinaryReader(partial))
        {
            decoder.Decode(reader, new RecordingHeaderListener());
        }

        var listener = new RecordingHeaderListener();
        using (var rest = new MemoryStream(new byte[]
               { 0x01, 0x00, 0x03, (byte)'a', (byte)'b', (byte)'c', 0x01, (byte)'d' }))
        using (var reader = new BinaryReader(rest))
        {
            decoder.Decode(reader, listener);
            decoder.EndHeaderBlock();
        }

        Assert.AreEqual(32, decoder.GetMaxHeaderTableSize());
        Assert.AreEqual(1, listener.Headers.Count);
        Assert.AreEqual("abc", listener.Headers[0].Item1);
        Assert.AreEqual("d", listener.Headers[0].Item2);
    }

    private static void WriteHuffmanString(BinaryWriter writer, byte[] data)
    {
        var huffLen = HuffmanEncoder.Instance.GetEncodedLength(new ByteString(data));
        // length prefix with H bit set; for short lengths (<127) one byte: 0x80 | len
        Assert.IsTrue(huffLen < 127);
        writer.Write((byte)(0x80 | huffLen));
        HuffmanEncoder.Instance.Encode(writer, new ByteString(data));
    }

    private static ByteString Bs(string s) => new(Encoding.ASCII.GetBytes(s));

    private sealed class RecordingHeaderListener : IHeaderListener
    {
        internal System.Collections.Generic.List<Tuple<string, string>> Headers { get; } = new();

        public void AddHeader(ByteString name, ByteString value, bool sensitive)
            => Headers.Add(Tuple.Create(name.ToString(), value.ToString()));
    }

    private static async Task<Http2OriginConnection> CreateOriginConnectionShellAsync()
    {
        // ProxyServer must outlive the connection (Dispose updates server connection counts).
        var proxy = new ProxyServer(false, false, false);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await accept;
        accepted.Dispose();

        var stream = new HttpServerStream(proxy, new NetworkStream(client, ownsSocket: true),
            new DefaultBufferPool(), CancellationToken.None);
        var serverConn = new TcpServerConnection(proxy, client, stream, "origin.test", 443, true,
            default, HttpHeader.Version20, null, null, "h2-origin");

        var ctor = typeof(Http2OriginConnection).GetConstructor(PrivateInstance, null,
            [typeof(TcpServerConnection), typeof(Microsoft.Extensions.Logging.ILogger), typeof(long),
                typeof(ProxyResourceLimits)], null)!;
        return (Http2OriginConnection)ctor.Invoke([serverConn, NullLogger.Instance, 1024L * 1024L,
            ProxyResourceLimits.Default])!;
    }
}
