using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Unknown-length (chunked / H2 without Content-Length) bodies must never be buffered against the
/// proxy's hard abort budget. They are relayed and previewed through a bounded, decoded tee,
/// independent of content type or host.
/// </summary>
[TestClass]
public class InterceptionUnknownLengthStreamingTests
{
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    private static (ProxyServer Proxy, SessionEventArgs Session, CancellationTokenSource Cts) CreateSession()
    {
        var proxy = new ProxyServer(userTrustRootCertificate: false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.Method = "POST";
        return (proxy, session, cts);
    }

    [TestMethod]
    public void ShouldBufferBody_BuffersOnlyKnownLengthWithinTheAbortBudget()
    {
        var (proxy, session, cts) = CreateSession();
        using var _p = proxy;
        using var _c = cts;
        using var _s = session;
        using var interception = new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true };
        var shouldBuffer = typeof(InterceptionService).GetMethod("ShouldBufferBody", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        const int abortBudget = 32 * 1024 * 1024;
        session.MaxBufferedBodyBytes = abortBudget;

        // Known length within the budget is buffered, request and JSON response alike.
        session.HttpClient.Request.ContentLength = 100;
        Assert.IsTrue((bool)shouldBuffer.Invoke(null, [session.HttpClient.Request, session])!);
        session.HttpClient.Response.ContentType = "application/json";
        session.HttpClient.Response.ContentLength = 100;
        Assert.IsTrue((bool)shouldBuffer.Invoke(null, [session.HttpClient.Response, session])!);

        // Known length past the abort budget is relayed, not buffered.
        session.HttpClient.Request.ContentLength = abortBudget + 1L;
        Assert.IsFalse((bool)shouldBuffer.Invoke(null, [session.HttpClient.Request, session])!);
        session.HttpClient.Response.ContentLength = abortBudget + 1L;
        Assert.IsFalse((bool)shouldBuffer.Invoke(null, [session.HttpClient.Response, session])!);

        // Unknown length is never buffered, including a git push pack and a git clone pack result.
        session.HttpClient.Request.RequestUriString = "https://github.com/org/repo.git/git-receive-pack";
        session.HttpClient.Request.ContentType = "application/x-git-receive-pack-request";
        session.HttpClient.Request.ContentLength = -1;
        Assert.IsFalse((bool)shouldBuffer.Invoke(null, [session.HttpClient.Request, session])!);

        session.HttpClient.Response.ContentType = "application/x-git-upload-pack-result";
        session.HttpClient.Response.ContentLength = -1;
        Assert.IsFalse((bool)shouldBuffer.Invoke(null, [session.HttpClient.Response, session])!);

        session.HttpClient.Response.ContentType = "text/event-stream";
        Assert.IsFalse((bool)shouldBuffer.Invoke(null, [session.HttpClient.Response, session])!);

        // Chunked JSON is the same rule: no declared length, so it is streamed.
        session.HttpClient.Response.ContentType = "application/json";
        session.HttpClient.Response.IsChunked = true;
        Assert.IsFalse((bool)shouldBuffer.Invoke(null, [session.HttpClient.Response, session])!);
    }

    [TestMethod]
    public void ApplyResponseBodyCapture_UnknownLengthUnread_IsStreamingRegardlessOfContentType()
    {
        var apply = typeof(InterceptionService).GetMethod("ApplyResponseBodyCapture", PrivateStatic)!;
        var req = new Request { Method = "GET" };

        foreach (var contentType in new[] { "application/json", "application/octet-stream", "application/x-any-binary", "text/event-stream" })
        {
            var snap = new SessionSnapshot();
            var resp = new Response { StatusCode = 200, ContentType = contentType };
            resp.Headers.AddHeader("Content-Encoding", "gzip");
            resp.IsChunked = true;
            apply.Invoke(null, [snap, resp, req, null]);

            Assert.AreEqual(BodyCaptureState.Streaming, snap.ResponseBodyCapture, contentType);
            Assert.IsTrue(snap.ResponseBodyStreamOpen, contentType);
            Assert.AreEqual("gzip", snap.ResponseContentEncoding, contentType);
        }
    }

    [TestMethod]
    public void ApplyRequestBodyCapture_UnknownLengthUnread_IsStreaming_KnownOrEmptyAreNot()
    {
        var apply = typeof(InterceptionService).GetMethod("ApplyRequestBodyCapture", PrivateStatic)!;

        var snap = new SessionSnapshot();
        var chunked = new Request { Method = "POST" };
        chunked.Headers.AddHeader("Content-Encoding", "br");
        chunked.IsChunked = true;
        apply.Invoke(null, [snap, chunked, null]);
        Assert.AreEqual(BodyCaptureState.Streaming, snap.RequestBodyCapture);
        Assert.AreEqual("br", snap.RequestContentEncoding);

        snap = new SessionSnapshot();
        var empty = new Request { Method = "POST" };
        empty.ContentLength = 0;
        apply.Invoke(null, [snap, empty, null]);
        Assert.AreEqual(BodyCaptureState.None, snap.RequestBodyCapture);

        snap = new SessionSnapshot();
        var known = new Request { Method = "POST" };
        known.ContentLength = 128;
        apply.Invoke(null, [snap, known, null]);
        Assert.AreNotEqual(BodyCaptureState.Streaming, snap.RequestBodyCapture);
    }

    [TestMethod]
    public void ResponseTee_GzipChunkedJson_IsDecodedAndComplete()
    {
        var (proxy, session, cts) = CreateSession();
        using var _p = proxy;
        using var _c = cts;
        using var _s = session;
        using var interception = new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true };
        var tee = typeof(InterceptionService).GetMethod("TeeResponseChunk", PrivateInstance)!;

        const string json = "{\"items\":[1,2,3],\"ok\":true}";
        var wire = Gzip(Encoding.UTF8.GetBytes(json));
        var split = wire.Length / 2;

        var snap = new SessionSnapshot
        {
            ResponseBodyCapture = BodyCaptureState.Streaming,
            ResponseBodyStreamOpen = true,
            ResponseContentEncoding = "gzip",
        };
        tee.Invoke(interception, [snap, new BeforeBodyWriteEventArgs(session, wire[..split], isChunked: true, isLastChunk: false)]);
        tee.Invoke(interception, [snap, new BeforeBodyWriteEventArgs(session, wire[split..], isChunked: true, isLastChunk: true)]);

        Assert.AreEqual(BodyCaptureState.Complete, snap.ResponseBodyCapture);
        Assert.IsFalse(snap.ResponseBodyStreamOpen);
        Assert.AreEqual(json, snap.ResponseBodyText);
        Assert.AreEqual((long)wire.Length, snap.ResponseBodyOriginalSize);
        Assert.IsNull(snap.ResponseTeeStream);
    }

    [TestMethod]
    public void ResponseTee_LargeUnknownLengthBody_IsBoundedAndTruncated()
    {
        var (proxy, session, cts) = CreateSession();
        using var _p = proxy;
        using var _c = cts;
        using var _s = session;
        using var interception = new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true };
        var tee = typeof(InterceptionService).GetMethod("TeeResponseChunk", PrivateInstance)!;

        var snap = new SessionSnapshot
        {
            ResponseBodyCapture = BodyCaptureState.Streaming,
            ResponseBodyStreamOpen = true,
        };
        var chunk = new byte[1024 * 1024];
        const int chunks = 40; // 40 MiB: above the 32 MiB abort budget Inspector configures
        for (var i = 0; i < chunks; i++)
        {
            var args = new BeforeBodyWriteEventArgs(session, chunk, isChunked: true, isLastChunk: i == chunks - 1);
            tee.Invoke(interception, [snap, args]);
            if (i < chunks - 1)
            {
                Assert.IsTrue((snap.ResponseTeeStream?.Length ?? 0) <= InspectorBodyLimits.MaxBodyBytes);
            }
        }

        Assert.AreEqual(BodyCaptureState.Truncated, snap.ResponseBodyCapture);
        Assert.AreEqual(chunks * (long)chunk.Length, snap.ResponseBodyOriginalSize);
        Assert.AreEqual(InspectorBodyLimits.MaxBodyBytes, snap.ResponseBodyBytes!.Length);
    }

    [TestMethod]
    public void RequestTee_ChunkedUpload_IsTeedViaThrottleHook_AndFinalized()
    {
        var (proxy, session, cts) = CreateSession();
        using var _p = proxy;
        using var _c = cts;
        using var _s = session;
        using var interception = new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true };
        var throttle = typeof(InterceptionService).GetMethod("OnRequestBodyWriteThrottle", PrivateInstance)!;
        var live = (System.Collections.IDictionary)typeof(InterceptionService).GetField("_live", PrivateInstance)!.GetValue(interception)!;

        var snap = new SessionSnapshot { RequestBodyCapture = BodyCaptureState.Streaming };
        live[session.HttpClient] = snap;
        session.HttpClient.Request.IsBodyRead = false;

        var first = new BeforeBodyWriteEventArgs(session, "hello "u8.ToArray(), isChunked: true, isLastChunk: false);
        ((Task)throttle.Invoke(interception, [interception, first])!).GetAwaiter().GetResult();
        Assert.IsNotNull(snap.RequestTeeStream);

        var last = new BeforeBodyWriteEventArgs(session, "world"u8.ToArray(), isChunked: true, isLastChunk: true);
        ((Task)throttle.Invoke(interception, [interception, last])!).GetAwaiter().GetResult();

        Assert.AreEqual(BodyCaptureState.Complete, snap.RequestBodyCapture);
        Assert.AreEqual("hello world", snap.RequestBodyText);
        Assert.AreEqual(11L, snap.RequestBodyOriginalSize);
        Assert.IsNull(snap.RequestTeeStream);

        // A request that is not in Streaming state (buffered / known length) is never teed.
        var buffered = new SessionSnapshot { RequestBodyCapture = BodyCaptureState.Complete };
        live[session.HttpClient] = buffered;
        ((Task)throttle.Invoke(interception, [interception, first])!).GetAwaiter().GetResult();
        Assert.IsNull(buffered.RequestTeeStream);
    }

    [TestMethod]
    public void FinalizeStreamingRequestBody_NoBytes_ResetsToNone_AndDisposesTee()
    {
        var finalize = typeof(InterceptionService).GetMethod("FinalizeStreamingRequestBody", PrivateStatic)!;
        var snap = new SessionSnapshot
        {
            RequestBodyCapture = BodyCaptureState.Streaming,
            RequestTeeStream = new MemoryStream(),
        };
        finalize.Invoke(null, [snap]);
        Assert.AreEqual(BodyCaptureState.None, snap.RequestBodyCapture);
        Assert.IsNull(snap.RequestTeeStream);
    }

    [TestMethod]
    public void FinalizeStreamingBody_DerivesGrpcViewsFromStreamedPreview()
    {
        var finalize = typeof(InterceptionService).GetMethod("FinalizeStreamingBody", PrivateStatic)!;
        // gRPC frame: flag 0, length 2, payload 0x08 0x01 (protobuf field 1 = 1).
        byte[] frame = [0, 0, 0, 0, 2, 0x08, 0x01];
        var snap = new SessionSnapshot
        {
            IsGrpc = true,
            ResponseBodyCapture = BodyCaptureState.Streaming,
            ResponseBytesSeen = frame.Length,
            ResponseTeeStream = new MemoryStream(frame),
        };

        finalize.Invoke(null, [snap]);

        Assert.AreEqual(BodyCaptureState.Complete, snap.ResponseBodyCapture);
        Assert.IsNotNull(snap.GrpcFrames);
        Assert.AreEqual(1, snap.GrpcFrames!.Count);
        Assert.IsFalse(string.IsNullOrEmpty(snap.ProtobufDecodedText));
    }
}
