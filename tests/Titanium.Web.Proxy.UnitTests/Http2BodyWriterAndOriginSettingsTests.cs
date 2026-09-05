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
using HpackEncoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;

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
        using var cts = new CancellationTokenSource();
        var connectionState = new Http2ConnectionState(1, cts, 100);
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        // expectedLength=-1 → empty END_STREAM DATA after payload (chunked/unknown-length path).
        var writer = Activator.CreateInstance(writerType, PrivateInstance, binder: null,
            args: [1, connectionState, ms, flow, CancellationToken.None, -1L, 16384], culture: null)!;

        Assert.IsFalse((bool)writerType.GetProperty("CanRead")!.GetValue(writer)!);
        Assert.IsFalse((bool)writerType.GetProperty("CanSeek")!.GetValue(writer)!);
        Assert.IsTrue((bool)writerType.GetProperty("CanWrite")!.GetValue(writer)!);
        Assert.IsInstanceOfType<NotSupportedException>(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                _ = writerType.GetProperty("Length")!.GetValue(writer)).InnerException);
        Assert.IsInstanceOfType<NotSupportedException>(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                _ = writerType.GetProperty("Position")!.GetValue(writer)).InnerException);
        Assert.IsInstanceOfType<NotSupportedException>(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                writerType.GetProperty("Position")!.SetValue(writer, 0L)).InnerException);
        Assert.IsInstanceOfType<NotSupportedException>(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                writerType.GetMethod("Read", [typeof(byte[]), typeof(int), typeof(int)])!
                    .Invoke(writer, [new byte[1], 0, 1])).InnerException);
        Assert.IsInstanceOfType<NotSupportedException>(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                writerType.GetMethod("Seek")!.Invoke(writer, [0L, SeekOrigin.Begin])).InnerException);
        Assert.IsInstanceOfType<NotSupportedException>(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                writerType.GetMethod("SetLength")!.Invoke(writer, [0L])).InnerException);
        Assert.IsInstanceOfType<NotSupportedException>(
            Assert.ThrowsExactly<TargetInvocationException>(() =>
                writerType.GetMethod("Write", [typeof(byte[]), typeof(int), typeof(int)])!
                    .Invoke(writer, [new byte[1], 0, 1])).InnerException);

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

        // Frames are queued on the client write chain (no dedicated writer in this harness);
        // drain it before asserting the wire bytes.
        await connectionState.ClientWriteChain;

        var wire = ms.ToArray();
        Assert.IsTrue(wire.Length > 9 + 16384 + 9);
        Assert.AreEqual((byte)Http2FrameType.Data, wire[3]);
        // Final empty END_STREAM frame
        Assert.AreEqual((byte)Http2FrameType.Data, wire[wire.Length - 6]);
        Assert.AreEqual((byte)Http2FrameFlag.EndStream, wire[wire.Length - 5]);
    }

    [TestMethod]
    public async Task Http2BodyStreamWriter_KnownLength_PutsEndStreamOnLastData()
    {
        var writerType = typeof(Http2Helper).GetNestedType("Http2BodyStreamWriter", BindingFlags.NonPublic)!;
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource();
        var connectionState = new Http2ConnectionState(1, cts, 100);
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        const int length = 20000;
        var writer = Activator.CreateInstance(writerType, PrivateInstance, binder: null,
            args: [1, connectionState, ms, flow, CancellationToken.None, (long)length, 16384], culture: null)!;

        var payload = new byte[length];
        var writeAsync = writerType.GetMethod("WriteAsync",
            [typeof(byte[]), typeof(int), typeof(int), typeof(CancellationToken)])!;
        await (Task)writeAsync.Invoke(writer, [payload, 0, payload.Length, CancellationToken.None])!;

        var complete = writerType.GetMethod("CompleteAsync", BindingFlags.Instance | BindingFlags.NonPublic |
                                                            BindingFlags.Public)!;
        await (Task)complete.Invoke(writer, null)!;

        await connectionState.ClientWriteChain;

        var wire = ms.ToArray();
        // Two DATA frames (16384 + 3616); END_STREAM on the last payload frame — no empty trailer.
        Assert.AreEqual(9 + 16384 + 9 + (length - 16384), wire.Length);
        Assert.AreEqual((byte)Http2FrameType.Data, wire[3]);
        Assert.AreEqual(0, wire[4] & (byte)Http2FrameFlag.EndStream); // first frame not end
        Assert.AreEqual((byte)Http2FrameFlag.EndStream, wire[9 + 16384 + 4]);
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
        var apply = typeof(Http2OriginConnection).GetMethod("ApplySettings", PrivateInstance,
            binder: null, types: [typeof(byte[])], modifiers: null)!;

        // SETTINGS_MAX_FRAME_SIZE = 0x5, value = 100 (too small)
        var payload = new byte[] { 0x00, 0x05, 0x00, 0x00, 0x00, 100 };
        apply.Invoke(origin, [payload]);
        Assert.IsFalse(origin.IsUsable);
    }

    [TestMethod]
    public async Task ApplySettings_NegativeInitialWindowSize_FaultsConnection()
    {
        using var origin = await CreateOriginConnectionShellAsync();
        var apply = typeof(Http2OriginConnection).GetMethod("ApplySettings", PrivateInstance,
            binder: null, types: [typeof(byte[])], modifiers: null)!;

        // SETTINGS_INITIAL_WINDOW_SIZE = 0x4, value with high bit set → negative int
        var payload = new byte[] { 0x00, 0x04, 0x80, 0x00, 0x00, 0x00 };
        apply.Invoke(origin, [payload]);
        Assert.IsFalse(origin.IsUsable);
    }

    [TestMethod]
    public async Task ApplySettings_ValidMaxFrameAndWindow_UpdatesState()
    {
        using var origin = await CreateOriginConnectionShellAsync();
        var apply = typeof(Http2OriginConnection).GetMethod("ApplySettings", PrivateInstance,
            binder: null, types: [typeof(byte[])], modifiers: null)!;

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
    public void HeaderCollectorListener_CollectsStatusAndInterimFields()
    {
        var listenerType = typeof(Http2OriginConnection).GetNestedType("HeaderCollectorListener",
            BindingFlags.NonPublic)!;
        var listener = Activator.CreateInstance(listenerType, nonPublic: true)!;
        listenerType.GetMethod("Begin", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(listener, null);

        var add = listenerType.GetMethod("AddHeader")!;
        add.Invoke(listener, [Bs(":status"), Bs("100"), false]);
        add.Invoke(listener, [Bs("x-a"), Bs("1"), false]);
        add.Invoke(listener, [Bs("x-b"), Bs("2"), true]);

        var status = (ByteString)listenerType.GetField("Status",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(listener)!;
        Assert.AreEqual("100", Encoding.ASCII.GetString(status.Span));
        var interim = listenerType.GetField("InterimHeaders",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(listener);
        Assert.IsNotNull(interim);
        var headers = (HeaderCollection)interim!;
        Assert.IsTrue(headers.HeaderExists("x-a"));
        Assert.IsTrue(headers.HeaderExists("x-b"));
        Assert.AreEqual("1", headers.GetHeaders("x-a")![0].Value);
        Assert.AreEqual("2", headers.GetHeaders("x-b")![0].Value);
    }

    [TestMethod]
    public async Task ProcessHeaderBlock_Status100Then200_WritesInterimAndFinal()
    {
        using var origin = await CreateOriginConnectionShellAsync();
        var pendingType = typeof(Http2OriginConnection).GetNestedType("PendingStream", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(pendingType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [1024L], null)!;
        RegisterOpenedStream(origin, 1, pending);

        var process = GetProcessHeaderBlock(origin);
        process(1, EncodeStatusBlock(100, ("x-hint", "1")), false);

        var interimProp = pendingType.GetField("InterimChannel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        var interim = interimProp.GetValue(pending)!;
        var reader = interim.GetType().GetProperty("Reader")!.GetValue(interim)!;
        var tryRead = reader.GetType().GetMethod("TryRead")!;
        var readArgs = new object?[] { null };
        Assert.IsTrue((bool)tryRead.Invoke(reader, readArgs)!);
        var interimValue = readArgs[0]!;
        Assert.AreEqual(100, (int)interimValue.GetType().GetField("Item1")!.GetValue(interimValue)!);

        process(1, EncodeStatusBlock(200, ("content-type", "text/plain")), false);
        var response = (Response?)pendingType.GetField("Response", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(pending);
        Assert.IsNotNull(response);
        Assert.AreEqual(200, response!.StatusCode);
        Assert.AreEqual("text/plain", response.Headers.GetFirstHeader("content-type")?.Value);
    }

    [TestMethod]
    public async Task ProcessHeaderBlock_PassthroughLite_Drops1xxAndSignalsHeadersReceived()
    {
        using var origin = await CreateOriginConnectionShellAsync();
        var pendingType = typeof(Http2OriginConnection).GetNestedType("PendingStream", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(pendingType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [1024L, false], null)!;
        Assert.IsNull(pendingType.GetField("InterimChannel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(pending));

        RegisterOpenedStream(origin, 1, pending);

        var process = GetProcessHeaderBlock(origin);
        process(1, EncodeStatusBlock(100, ("x-hint", "1")), false);
        process(1, EncodeStatusBlock(200, ("content-type", "text/plain")), false);

        var response = (Response?)pendingType.GetField("Response", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(pending);
        Assert.IsNotNull(response);
        Assert.AreEqual(200, response!.StatusCode);

        var headersReceived = (TaskCompletionSource<bool>)pendingType
            .GetField("HeadersReceived", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(pending)!;
        Assert.IsTrue(headersReceived.Task.IsCompletedSuccessfully);
        Assert.IsTrue(await headersReceived.Task);
    }

    [TestMethod]
    public async Task ProcessHeaderBlock_TrailersWithoutStatus_Accumulate()
    {
        using var origin = await CreateOriginConnectionShellAsync();
        var pendingType = typeof(Http2OriginConnection).GetNestedType("PendingStream", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(pendingType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [1024L], null)!;
        RegisterOpenedStream(origin, 3, pending);

        var process = GetProcessHeaderBlock(origin);
        process(3, EncodeStatusBlock(200), false);
        process(3, EncodeLiteralBlock(("x-trailer", "t1")), true);

        var trailers = (HeaderCollection?)pendingType.GetField("TrailingHeaders",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(pending);
        Assert.IsNotNull(trailers);
        Assert.AreEqual("t1", trailers!.GetFirstHeader("x-trailer")?.Value);
    }

    [TestMethod]
    public async Task ProcessHeaderBlock_BadHpack_FaultsConnection()
    {
        using var origin = await CreateOriginConnectionShellAsync();
        var process = GetProcessHeaderBlock(origin);
        process(1, new byte[] { 0x80 }, false); // illegal indexed 0
        Assert.IsFalse(origin.IsUsable);
    }

    private static byte[] EncodeStatusBlock(int status, params (string Name, string Value)[] headers)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        var encoder = new HpackEncoder(4096);
        encoder.EncodeHeader(writer, Bs(":status"), Bs(status.ToString()), sensitive: false,
            HpackUtil.IndexType.None, useStaticName: true);
        foreach (var (name, value) in headers)
            encoder.EncodeHeader(writer, Bs(name), Bs(value), sensitive: false, HpackUtil.IndexType.None,
                useStaticName: true);
        return ms.ToArray();
    }

    private static byte[] EncodeLiteralBlock(params (string Name, string Value)[] headers)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        var encoder = new HpackEncoder(4096);
        foreach (var (name, value) in headers)
            encoder.EncodeHeader(writer, Bs(name), Bs(value), sensitive: false, HpackUtil.IndexType.None,
                useStaticName: true);
        return ms.ToArray();
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
        decoder.Decode(encoded.ToArray(), listener);
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
        decoder.Decode(new byte[] { 0x3F }, new RecordingHeaderListener());

        var listener = new RecordingHeaderListener();
        decoder.Decode(new byte[]
            { 0x01, 0x00, 0x03, (byte)'a', (byte)'b', (byte)'c', 0x01, (byte)'d' }, listener);
        decoder.EndHeaderBlock();

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

    private delegate void ProcessHeaderBlockDelegate(int streamId, ReadOnlySpan<byte> compressed, bool endStream);

    private static ProcessHeaderBlockDelegate GetProcessHeaderBlock(Http2OriginConnection origin)
    {
        var method = typeof(Http2OriginConnection).GetMethod("ProcessHeaderBlock", PrivateInstance)!;
        return method.CreateDelegate<ProcessHeaderBlockDelegate>(origin);
    }

    private static void RegisterOpenedStream(Http2OriginConnection origin, int streamId, object pending)
    {
        var method = typeof(Http2OriginConnection).GetMethod("RegisterOpenedStream", PrivateInstance)!;
        method.Invoke(origin, [streamId, pending]);
    }

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
