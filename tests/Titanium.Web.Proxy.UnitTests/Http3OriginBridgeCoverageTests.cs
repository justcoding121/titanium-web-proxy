#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http3OriginBridgeCoverageTests
{
    private static MethodInfo BridgeMethod(string name) =>
        typeof(Http3OriginBridge).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"HTTP/3 bridge method {name} was not found.");

    [TestMethod]
    public void BuildRequestHeaders_EmitsPseudoHeaders_StripsHopByHop_AndLowercases()
    {
        var request = new Request
        {
            Method = "POST",
            IsHttps = true,
            RequestUriString = "https://example.com:8443/path?q=1"
        };
        request.Headers.AddHeader("Connection", "close");
        request.Headers.AddHeader("Host", "wrong.example");
        request.Headers.AddHeader("Proxy-Authorization", "secret");
        request.Headers.AddHeader("X-Mixed", "kept");

        var headers = (List<(string Name, string Value)>)BridgeMethod("BuildRequestHeaders")
            .Invoke(null, [request, "fallback.example"])!;

        CollectionAssert.AreEqual(
            new[]
            {
                (":method", "POST"), (":scheme", "https"),
                (":authority", "example.com:8443"), (":path", "/path?q=1")
            },
            headers.Take(4).ToArray());
        Assert.IsFalse(headers.Any(h => h.Name is "connection" or "host" or "proxy-authorization"));
        Assert.IsTrue(headers.Contains(("x-mixed", "kept")));
    }

    [TestMethod]
    public void BuildRequestHeaders_HttpRoot_UsesHttpSchemeAndRootPath()
    {
        var request = new Request
        {
            Method = "OPTIONS",
            IsHttps = false,
            RequestUriString = "http://plain.example/"
        };

        var headers = (List<(string Name, string Value)>)BridgeMethod("BuildRequestHeaders")
            .Invoke(null, [request, "fallback.example"])!;

        Assert.IsTrue(headers.Contains((":scheme", "http")));
        Assert.IsTrue(headers.Contains((":authority", "plain.example")));
        Assert.IsTrue(headers.Contains((":path", "/")));
    }

    [TestMethod]
    public void ResponseHeaderHelpers_ParseStatus_IgnoreUnknownPseudo_AndKeepRegular()
    {
        var fields = new List<(string Name, string Value)>
        {
            (":unknown", "ignored"), (":status", "204"), ("x-origin", "yes")
        };

        var status = (int)BridgeMethod("ParseStatusCode").Invoke(null, [fields])!;
        var response = (Response)BridgeMethod("BuildResponseFromHeaders")
            .Invoke(null, [fields, HttpHeader.Version30])!;

        Assert.AreEqual(204, status);
        Assert.AreEqual(204, response.StatusCode);
        Assert.AreEqual(HttpHeader.Version30, response.HttpVersion);
        Assert.AreEqual("yes", response.Headers.GetHeaderValueOrNull("x-origin"));
        Assert.AreEqual(1, response.Headers.Count());
    }

    [TestMethod]
    public void ResponseHeaderHelpers_MissingOrInvalidStatus_ReturnsZero()
    {
        var fields = new List<(string Name, string Value)> { (":status", "not-a-number") };
        Assert.AreEqual(0, BridgeMethod("ParseStatusCode").Invoke(null, [fields]));

        fields.Clear();
        Assert.AreEqual(0, BridgeMethod("ParseStatusCode").Invoke(null, [fields]));
    }

    [TestMethod]
    public void MakeBadGatewayResponse_ContainsSafeHttp3Response()
    {
        var response = (Response)BridgeMethod("MakeBadGatewayResponse").Invoke(null, ["connection failed"])!;

        Assert.AreEqual(502, response.StatusCode);
        Assert.AreEqual(HttpHeader.Version30, response.HttpVersion);
        Assert.IsTrue(response.IsBodyRead);
        StringAssert.Contains(Encoding.UTF8.GetString(response.Body), "connection failed");
    }

    [TestMethod]
    public void BuildRequestHeaders_HostFallbackAndCleartextPath()
    {
        var request = new Request
        {
            Method = "GET",
            IsHttps = false,
            Host = "via-host.test",
        };
        var headers = (List<(string Name, string Value)>)BridgeMethod("BuildRequestHeaders")
            .Invoke(null, [request, "sni.example"])!;
        Assert.IsTrue(headers.Any(h => h.Name == ":scheme" && h.Value == "http"));
        Assert.IsTrue(headers.Any(h => h.Name == ":authority" && h.Value == "via-host.test"));

        var empty = new Request { Method = "HEAD" };
        var fallback = (List<(string Name, string Value)>)BridgeMethod("BuildRequestHeaders")
            .Invoke(null, [empty, "fallback.test"])!;
        Assert.IsTrue(fallback.Any(h => h.Name == ":authority" && h.Value == "fallback.test"));
        Assert.IsTrue(fallback.Any(h => h.Name == ":path" && h.Value == "/"));

        var fp = (int)BridgeMethod("ComputeOriginRequestQpackFingerprint")
            .Invoke(null, [request, "sni.example"])!;
        Assert.AreNotEqual(0, fp);
        var fpEmpty = (int)BridgeMethod("ComputeOriginRequestQpackFingerprint")
            .Invoke(null, [empty, "fallback.test"])!;
        Assert.AreNotEqual(0, fpEmpty);
    }

    [TestMethod]
    [DataRow((ulong)Http3FrameType.Settings, true)]
    [DataRow((ulong)Http3FrameType.GoAway, true)]
    [DataRow((ulong)Http3FrameType.MaxPushId, true)]
    [DataRow((ulong)Http3FrameType.CancelPush, true)]
    [DataRow((ulong)Http3FrameType.Data, false)]
    [DataRow(0x21UL, false)]
    public void IsForbiddenOnRequestStream_ClassifiesFrameTypes(ulong frameType, bool expected)
    {
        var actual = (bool)BridgeMethod("IsForbiddenOnRequestStream").Invoke(null, [frameType])!;
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void MitmQuicHelpers_PopulateAppendAndCapture()
    {
        var qpack = QpackEncoder.Encode(
        [
            (":status", "203"),
            (":unknown", "skip"),
            ("x-origin", "yes"),
        ]);

        var populate = BridgeMethod("PopulateResponseFromQpack")
            .CreateDelegate<PopulateResponseFromQpackDelegate>();
        var response = new Response();
        populate(response, qpack);
        Assert.AreEqual(HttpHeader.Version30, response.HttpVersion);
        Assert.AreEqual(203, response.StatusCode);
        Assert.AreEqual("yes", response.Headers.GetHeaderValueOrNull("x-origin"));
        Assert.IsNull(response.Headers.GetHeaderValueOrNull(":unknown"));

        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var fwd = new H3H2FastForward
        {
            Request = new Request { Method = "GET" },
            Response = new Response(),
            ProxyEndPoint = ep,
        };
        BridgeMethod("AppendMitmQuicBody").Invoke(null, [fwd, ReadOnlyMemory<byte>.Empty]);
        Assert.IsNull(fwd.PreencodedBody);

        BridgeMethod("AppendMitmQuicBody").Invoke(null, [fwd, new ReadOnlyMemory<byte>([1, 2])]);
        Assert.AreEqual(2, fwd.PreencodedBodyLength);
        Assert.IsTrue(fwd.Response!.IsBodyRead);
        BridgeMethod("AppendMitmQuicBody").Invoke(null, [fwd, new ReadOnlyMemory<byte>([3])]);
        Assert.AreEqual(3, fwd.PreencodedBodyLength);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, fwd.Response.Body);

        var emptyFwd = new H3H2FastForward
        {
            Request = new Request { Method = "GET" },
            Response = new Response(),
            ProxyEndPoint = ep,
        };
        BridgeMethod("CaptureMitmQuicResponse").Invoke(null,
            [emptyFwd, new ReadOnlyMemory<byte>(qpack), ReadOnlyMemory<byte>.Empty]);
        Assert.IsNotNull(emptyFwd.PreencodedQpackHeaders);
        Assert.AreEqual(203, emptyFwd.Response!.StatusCode);
        Assert.AreEqual(0, emptyFwd.PreencodedBodyLength);
        Assert.IsTrue(emptyFwd.Response.IsBodyReceived);

        var bodyFwd = new H3H2FastForward
        {
            Request = new Request { Method = "GET" },
            Response = new Response(),
            ProxyEndPoint = ep,
        };
        BridgeMethod("CaptureMitmQuicResponse").Invoke(null,
            [bodyFwd, new ReadOnlyMemory<byte>(qpack), new ReadOnlyMemory<byte>([9, 8])]);
        Assert.AreEqual(2, bodyFwd.PreencodedBodyLength);
        CollectionAssert.AreEqual(new byte[] { 9, 8 }, bodyFwd.Response!.Body);
        Assert.IsTrue(bodyFwd.Response.BodyIsWireEncoded);
    }

    [TestMethod]
    public async Task EnsureHttp3BufferedBodyAsync_EarlyReturnsWhenAlreadyReceivedOrNoReader()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433),
            new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        using var session = new SessionEventArgs(proxy, ep, clientStream, null, cts);
        session.HttpClient.Request.IsBodyReceived = true;
        await (Task)BridgeMethod("EnsureHttp3BufferedBodyAsync")
            .Invoke(null, [session, CancellationToken.None])!;
        Assert.IsTrue(session.HttpClient.Request.IsBodyReceived);

        session.HttpClient.Request.IsBodyReceived = false;
        session.Http3BufferedBodyReader = null;
        await (Task)BridgeMethod("EnsureHttp3BufferedBodyAsync")
            .Invoke(null, [session, CancellationToken.None])!;
        Assert.IsFalse(session.HttpClient.Request.IsBodyReceived);
        Assert.IsNull(session.Http3BufferedBodyReader);
    }

    private delegate void PopulateResponseFromQpackDelegate(Response response, ReadOnlySpan<byte> qpack);

    [TestMethod]
    public async Task EncodeOriginRequestHeaders_DetachedQuic_UsesQpackAndAuthorityHelpers()
    {
        using var proxy = new ProxyServer(false, false, false);
        await using var detached = QuicServerConnection.CreateDetachedForTests(proxy, "sni.example", 443, "cache");
        var request = new Request
        {
            Method = "GET",
            IsHttps = true,
            RequestUriString8 = "/q".GetByteString(),
            Authority = "auth.example".GetByteString(),
        };
        request.Headers.AddHeader("X-Keep", "1");

        var encoded = (byte[])BridgeMethod("EncodeOriginRequestHeaders")
            .Invoke(null, [detached, request, "sni.example"])!;
        Assert.IsTrue(encoded.Length > 0);

        var hostOnly = new Request { Method = "HEAD", Host = "host.example" };
        var fp = (int)BridgeMethod("ComputeOriginRequestQpackFingerprint")
            .Invoke(null, [hostOnly, "sni.example"])!;
        Assert.AreNotEqual(0, fp);

        var empty = new Request { Method = "OPTIONS" };
        var fpEmpty = (int)BridgeMethod("ComputeOriginRequestQpackFingerprint")
            .Invoke(null, [empty, "fallback.example"])!;
        Assert.AreNotEqual(0, fpEmpty);

        // Encode again (still null session) — covers QpackEncoder path without cache hit.
        var again = (byte[])BridgeMethod("EncodeOriginRequestHeaders")
            .Invoke(null, [detached, hostOnly, "sni.example"])!;
        Assert.IsTrue(again.Length > 0);
    }

    [TestMethod]
    public void CanReplayHttp2OriginRequest_RejectsStreamingBodyWithoutBuffer()
    {
        var request = new Request { Method = "POST", ContentLength = 4 };
        request.IsBodyReceived = false;
        request.IsBodyRead = false;
        Assert.IsTrue(request.HasBody);
        Func<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task> pump =
            (_, _) => Task.CompletedTask;
        Assert.IsFalse((bool)BridgeMethod("CanReplayHttp2OriginRequest").Invoke(null, [request, pump])!);

        request.IsBodyRead = true;
        Assert.IsTrue((bool)BridgeMethod("CanReplayHttp2OriginRequest").Invoke(null, [request, pump])!);

        var noBody = new Request { Method = "GET" };
        Assert.IsTrue((bool)BridgeMethod("CanReplayHttp2OriginRequest").Invoke(null, [noBody, pump])!);
    }

    [TestMethod]
    public void ResolveTransparentForwardTarget_EmptyForwardHost_ReturnsNull()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false) { ForwardHost = "" };
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        using var session = new SessionEventArgs(proxy, ep, clientStream, null, cts);
        var fwd = ((string? Host, int? Port))BridgeMethod("ResolveTransparentForwardTarget")
            .Invoke(null, [session])!;
        Assert.IsNull(fwd.Host);
        Assert.IsNull(fwd.Port);
    }

}
#pragma warning restore CA1416
