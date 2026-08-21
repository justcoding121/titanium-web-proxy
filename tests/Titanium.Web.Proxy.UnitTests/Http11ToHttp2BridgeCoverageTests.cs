using System;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http11ToHttp2BridgeCoverageTests
{
    private static MethodInfo PrivateBridgeMethod(string name) =>
        typeof(ProxyServer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Bridge method {name} was not found.");

    private static void PrepareRequest(Request request, bool webSocket = false)
    {
        var name = webSocket
            ? "PrepareWebSocketUpgradeForHttp2Origin"
            : "PrepareRequestForOrigin";
        PrivateBridgeMethod(name).Invoke(null, [request]);
    }

    private static bool ClientRequestsClose(Request request) =>
        (bool)PrivateBridgeMethod("ClientRequestedConnectionClose").Invoke(null, [request])!;

    [TestMethod]
    public void PrepareRequestForOrigin_CapturesAuthority_StripsHost_AndLowercases()
    {
        var request = new Request
        {
            Method = "POST",
            HttpVersion = HttpHeader.Version11,
            RequestUriString = "https://origin.example/resource"
        };
        request.Headers.AddHeader(KnownHeaders.Host, "origin.example:8443");
        request.Headers.AddHeader(KnownHeaders.Connection, "keep-alive");
        request.Headers.AddHeader("Keep-Alive", "timeout=5");
        request.Headers.AddHeader(KnownHeaders.ProxyConnection, "keep-alive");
        request.Headers.AddHeader(KnownHeaders.TransferEncoding, "chunked");
        request.Headers.AddHeader(KnownHeaders.Upgrade, "websocket");
        request.Headers.AddHeader("TE", "trailers");
        request.Headers.AddHeader("X-Mixed-Case", "kept");

        PrepareRequest(request);

        Assert.AreEqual("origin.example:8443", request.Authority.GetString());
        Assert.IsNull(request.Headers.GetHeaderValueOrNull("host"), "host must be removed.");
        // Hop-by-hop stay on the Request for ClientRequestedConnectionClose / diagnostics;
        // EncodeHeaderBlock.ShouldOmitHttp2Header drops them on the wire.
        Assert.IsNotNull(request.Headers.GetHeaderValueOrNull("connection"));
        Assert.AreEqual("kept", request.Headers.GetHeaderValueOrNull("x-mixed-case"));
        Assert.IsTrue(request.Headers.All(h =>
            string.Equals(h.Name, h.Name.ToLowerInvariant(), StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PrepareRequestForOrigin_PreservesExistingAuthority()
    {
        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version11,
            RequestUriString = "/",
            Authority = "preserved.example:443".GetByteString()
        };
        request.Headers.AddHeader(KnownHeaders.Host, "discarded.example");

        PrepareRequest(request);

        Assert.AreEqual("preserved.example:443", request.Authority.GetString());
        Assert.IsNull(request.Host);
    }

    [TestMethod]
    public void PrepareWebSocketUpgradeForHttp2Origin_ConvertsToExtendedConnect()
    {
        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version11,
            RequestUriString = "https://ws.example/chat"
        };
        request.Headers.AddHeader(KnownHeaders.Host, "ws.example");
        request.Headers.AddHeader(KnownHeaders.Connection, "Upgrade");
        request.Headers.AddHeader(KnownHeaders.Upgrade, "websocket");
        request.Headers.AddHeader("Sec-WebSocket-Key", "key");
        request.Headers.AddHeader("Sec-WebSocket-Accept", "response-only");
        request.Headers.AddHeader("Sec-WebSocket-Protocol", "chat");

        PrepareRequest(request, webSocket: true);

        Assert.AreEqual("CONNECT", request.Method);
        Assert.AreEqual("websocket", request.ExtendedConnectProtocol);
        Assert.AreEqual("ws.example", request.Authority.GetString());
        Assert.IsNull(request.Headers.GetHeaderValueOrNull("sec-websocket-key"));
        Assert.IsNull(request.Headers.GetHeaderValueOrNull("sec-websocket-accept"));
        Assert.AreEqual("chat", request.Headers.GetHeaderValueOrNull("sec-websocket-protocol"));
    }

    [TestMethod]
    public void ClientRequestedConnectionClose_AppliesHttp10AndHttp11Defaults()
    {
        var http10 = new Request { HttpVersion = HttpHeader.Version10 };
        Assert.IsTrue(ClientRequestsClose(http10), "HTTP/1.0 closes unless keep-alive is explicit.");
        http10.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionKeepAlive.String);
        Assert.IsFalse(ClientRequestsClose(http10));

        var http11 = new Request { HttpVersion = HttpHeader.Version11 };
        Assert.IsFalse(ClientRequestsClose(http11), "HTTP/1.1 persists unless close is explicit.");
        http11.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionClose.String);
        Assert.IsTrue(ClientRequestsClose(http11));
    }

    [TestMethod]
    public void BuildSwitchingProtocolsResponse_ComputesAcceptAndCopiesNegotiatedHeadersOnly()
    {
        const string key = "dGhlIHNhbXBsZSBub25jZQ==";
        var origin = new Response { StatusCode = 200 };
        origin.Headers.AddHeader("Sec-WebSocket-Protocol", "chat");
        origin.Headers.AddHeader("Sec-WebSocket-Extensions", "permessage-deflate");
        origin.Headers.AddHeader("X-Origin-Only", "excluded");

        var response = (Response)PrivateBridgeMethod("BuildSwitchingProtocolsResponse")
            .Invoke(null, [key, origin])!;

        Assert.AreEqual(101, response.StatusCode);
        Assert.AreEqual(HttpHeader.Version11, response.HttpVersion);
        Assert.AreEqual("websocket", response.Headers.GetHeaderValueOrNull(KnownHeaders.Upgrade));
        Assert.AreEqual("Upgrade", response.Headers.GetHeaderValueOrNull(KnownHeaders.Connection));
        Assert.AreEqual("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=",
            response.Headers.GetHeaderValueOrNull("Sec-WebSocket-Accept"));
        Assert.AreEqual("chat", response.Headers.GetHeaderValueOrNull("Sec-WebSocket-Protocol"));
        Assert.AreEqual("permessage-deflate",
            response.Headers.GetHeaderValueOrNull("Sec-WebSocket-Extensions"));
        Assert.IsNull(response.Headers.GetHeaderValueOrNull("X-Origin-Only"));
    }
}
