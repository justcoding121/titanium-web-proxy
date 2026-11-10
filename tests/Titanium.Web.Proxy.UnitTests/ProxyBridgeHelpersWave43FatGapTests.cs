#pragma warning disable TWP001
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat H1↔H2 / H2↔H3 bridge helpers and <see cref="HeaderBuilder" /> still on the Copy.Headers hot path.
/// </summary>
[TestClass]
public class ProxyBridgeHelpersWave43FatGapTests
{
    [TestMethod]
    public void HeaderBuilder_CoversVersionEmptyUrlAndProxyAuthBranches()
    {
        var builder = HeaderBuilder.Rent();
        try
        {
            builder.WriteRequestLine("GET", "", HttpHeader.Version10);
            builder.WriteRequestLine("OPTIONS", (ByteString)"", new Version(1, 2));
            builder.WriteResponseLine(HttpHeader.Version11, 200, "OK");
            builder.WriteResponseLine(new Version(2, 0), 418, "I'm a teapot");

            var headers = new HeaderCollection();
            headers.AddHeader("Proxy-Authorization", "secret");
            headers.AddHeader("X-Keep", "1");
            builder.WriteHeaders(headers, sendProxyAuthorization: false);
            builder.WriteHeaders(headers, sendProxyAuthorization: true);
            builder.WriteHeaders(headers, sendProxyAuthorization: true, "user", "pass");
            builder.WriteLine();
            builder.WriteRaw("raw"u8);
            builder.Write(string.Empty);
            builder.Write(new string('x', 300));

            var buf = builder.GetBuffer();
            Assert.IsTrue(buf.Count > 20);
            Assert.IsFalse(string.IsNullOrEmpty(builder.GetString(Encoding.ASCII)));
        }
        finally
        {
            HeaderBuilder.Return(builder);
            HeaderBuilder.Return(HeaderBuilder.Rent());
        }
    }

    [TestMethod]
    public async Task Http2ToHttp11_ReadLineAndLiteHelpers()
    {
        var readLine = Wave42FatGapTestSupport.ProxyMethod("ReadLineAsync");
        await using (var ms = new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n")))
        {
            var line = await (Task<string?>)readLine.Invoke(null, [ms, CancellationToken.None])!;
            Assert.AreEqual("HTTP/1.1 200 OK", line);
        }

        await using (var partial = new MemoryStream(Encoding.ASCII.GetBytes("no-nl")))
        {
            Assert.AreEqual("no-nl", await (Task<string?>)readLine.Invoke(null, [partial, CancellationToken.None])!);
        }

        var lite = Wave42FatGapTestSupport.ProxyMethod("IsH2BridgeLiteMethod");
        Assert.IsTrue((bool)lite.Invoke(null, ["GET"])!);
        Assert.IsFalse((bool)lite.Invoke(null, ["POST"])!);

        var hasUpper = Wave42FatGapTestSupport.ProxyMethod("HeaderNameDataHasUpperCaseAscii");
        Assert.IsTrue((bool)hasUpper.Invoke(null, ["Content-Type".GetByteString()])!);
        Assert.IsFalse((bool)hasUpper.Invoke(null, ["content-type".GetByteString()])!);
    }

    [TestMethod]
    public void Http11ToHttp2_PrepareOriginAndWsHelpers()
    {
        var prepare = Wave42FatGapTestSupport.ProxyMethod("PrepareRequestForOrigin");
        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version11,
            Host = "Example.COM",
            RequestUriString8 = "/path".GetByteString()
        };
        request.Headers.AddHeader("X-Mixed", "1");
        prepare.Invoke(null, [request]);
        Assert.IsTrue(request.HeaderNamesAreHttp2Normalized);

        var wsPrep = Wave42FatGapTestSupport.ProxyMethod("PrepareWebSocketUpgradeForHttp2Origin");
        var ws = new Request
        {
            Method = "GET",
            Host = "ws.example",
            RequestUriString8 = "/chat".GetByteString()
        };
        ws.Headers.AddHeader(KnownHeaders.Upgrade, KnownHeaders.UpgradeWebsocket);
        ws.Headers.AddHeader(KnownHeaders.Connection, "Upgrade");
        ws.Headers.AddHeader("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        ws.Headers.AddHeader("Sec-WebSocket-Version", "13");
        wsPrep.Invoke(null, [ws]);
        Assert.AreEqual("CONNECT", ws.Method);
        Assert.AreEqual("websocket", ws.ExtendedConnectProtocol);
    }

    [TestMethod]
    public void Http2ToHttp3_CookieConsolidateAndOriginIdentity()
    {
        var consolidate = Wave42FatGapTestSupport.ProxyMethod("ConsolidateCookieHeaders");
        var cookies = new HeaderCollection();
        cookies.AddHeader("Cookie", "a=1");
        cookies.AddHeader("Cookie", "b=2");
        consolidate.Invoke(null, [cookies]);
        Assert.AreEqual("a=1; b=2", cookies.GetHeaderValueOrNull("Cookie"));

        using var proxy = new ProxyServer(false, false, false);
        var resolve = Wave42FatGapTestSupport.ProxyMethod("ResolveH3BridgeOriginIdentity");
        var transparent = new TransparentProxyEndPoint(IPAddress.Loopback, 0, true)
        {
            ForwardHost = "fwd.example",
            ForwardPort = 8443
        };
        using var session = Wave42FatGapTestSupport.MakeSession(proxy, transparent);
        var identity = ((string Host, int Port))resolve.Invoke(null, [session, "fallback.example", 443])!;
        Assert.AreEqual("fwd.example", identity.Host);
        Assert.AreEqual(8443, identity.Port);
    }

    [TestMethod]
    public void Http3Bridge_PrepareReplayBuildRequestHeaders()
    {
        var request = new Request
        {
            Method = "POST",
            Host = "Host.Example",
            HttpVersion = HttpHeader.Version20
        };
        request.Headers.AddHeader("Connection", "keep-alive");
        request.Headers.AddHeader("X-Mixed", "kept");
        Wave42FatGapTestSupport.BridgeMethod("PrepareH2OriginRequestHeaders").Invoke(null, [request]);
        Assert.AreEqual("Host.Example", request.Authority.GetString());
        Assert.IsNull(request.Headers.GetHeaderValueOrNull("connection"));

        Assert.IsTrue((bool)Wave42FatGapTestSupport.BridgeMethod("CanReplayHttp2OriginRequest")
            .Invoke(null, [request, null])!);

        var built = (List<(string, string)>)Wave42FatGapTestSupport.BridgeMethod("BuildRequestHeaders")
            .Invoke(null, [new Request { Method = "GET", Host = "host-only.example" }, "fallback.example"])!;
        Assert.IsTrue(built.Exists(t => t is (":authority", "host-only.example")));
    }
}
#pragma warning restore TWP001
