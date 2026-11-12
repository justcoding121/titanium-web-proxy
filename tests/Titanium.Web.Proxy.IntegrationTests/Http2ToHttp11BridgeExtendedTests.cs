using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Extended coverage for the HTTP/2-to-HTTP/1.1 bridge
///     (<see cref="Http2ToHttp11BridgeHandler" /> / <c>RunHttp2ToHttp11BridgeAttemptAsync</c>).
///     Each test targets a distinct bug class not yet exercised by existing H2 or bridge tests:
///     body buffering for non-GET methods, 204/304 no-body responses, coalesced Cookie headers,
///     multiple concurrent streams, BeforeResponse status override, and HEAD requests.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http2ToHttp11BridgeExtendedTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Endpoint) StartBridgeProxy(
        TestSuite suite, Action<ProxyServer>? configure = null)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        configure?.Invoke(proxy);

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, endpoint);
    }

    private static async Task<(string Status, string Body)> SendH2RequestAsync(
        Http2RawClient raw,
        int streamId,
        string method,
        string path,
        (string Name, string Value)[]? extraHeaders = null,
        byte[]? requestBody = null)
    {
        var pseudo = new[]
        {
            (":method", method),
            (":scheme", "https"),
            (":authority", "localhost"),
            (":path", path),
        };
        var regular = Array.Empty<(string, string)>();
        if (requestBody is { Length: > 0 })
            regular = regular.Append(("content-length", requestBody.Length.ToString())).ToArray();
        if (extraHeaders is not null)
            regular = regular.Concat(extraHeaders).ToArray();

        var block = raw.Connection.EncodeHeaders(pseudo, regular);
        var endHeaders = requestBody is null || requestBody.Length == 0;
        await raw.Connection.WriteHeaderBlockAsync(streamId, block, endHeaders);
        if (!endHeaders)
            await raw.Connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, requestBody!);

        var (_, responseHeaders, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        var status = responseHeaders.Single(h => h.Name == ":status").Value;

        var body = new MemoryStream();
        if (!endStream)
        {
            Http2RawFrame.Frame frame;
            do
            {
                frame = await raw.Connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
                    body.Write(frame.Payload, 0, frame.Payload.Length);
            } while (!(frame.Type == Http2FrameType.Data && (frame.Flags & Http2FrameFlag.EndStream) != 0));
        }

        return (status, Encoding.UTF8.GetString(body.ToArray()));
    }

    // ---------------------------------------------------------------- POST with body

    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_Post_WithBody_RelaysToOriginAndReturnsResponse()
    {
        // Bug class: H2 POST body is fully buffered before the bridge opens the H1.1 connection.
        // A missing body or premature end-of-stream on the H1.1 side surfaces here.
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        var received = string.Empty;
        server.HandleRequest(async ctx =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            received = await sr.ReadToEndAsync();
            ctx.Response.StatusCode = 201;
            await ctx.Response.WriteAsync("created");
        });

        var (_, endpoint) = StartBridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var body = Encoding.UTF8.GetBytes("hello-post-body");
        var (status, respBody) = await SendH2RequestAsync(raw, 1, "POST", "/resource",
            requestBody: body);

        Assert.AreEqual("201", status);
        Assert.AreEqual("created", respBody);
        Assert.AreEqual("hello-post-body", received,
            "The H2→H1.1 bridge must relay the request body to the HTTP/1.1 origin.");
    }

    // ---------------------------------------------------------------- 204 No Content (no body)

    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_204_NoContent_IsRelayedWithNoDataFrame()
    {
        // Bug class: a 204 response has no body; writing a DATA frame for it desyncs H1.1 keep-alive.
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        });

        var (_, endpoint) = StartBridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "DELETE"), (":scheme", "https"), (":authority", "localhost"), (":path", "/item/1")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("204", headers.Single(h => h.Name == ":status").Value);
        Assert.IsTrue(endStream, "A 204 response must arrive with END_STREAM on the HEADERS frame or immediately after via an empty DATA frame.");
    }

    // ---------------------------------------------------------------- Merged Cookie header

    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_MultipleCookieHeaders_AreMergedIntoOne()
    {
        // Bug class: RFC 7540 §8.1.2.5 — h2 may split Cookie across multiple HEADERS lines;
        // HTTP/1.1 must receive a single combined Cookie header.
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        var cookieHeader = string.Empty;
        server.HandleRequest(ctx =>
        {
            cookieHeader = ctx.Request.Headers.Cookie.ToString();
            return ctx.Response.WriteAsync("ok");
        });

        var (_, endpoint) = StartBridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        // Two separate Cookie entries — valid h2 per RFC 7540 §8.1.2.5.
        var (status, body) = await SendH2RequestAsync(raw, 1, "GET", "/",
            extraHeaders: [("cookie", "a=1"), ("cookie", "b=2")]);

        Assert.AreEqual("200", status);
        Assert.AreEqual("ok", body);
        // After merging they must appear joined with "; " as RFC 7540 §8.1.2.5 requires.
        Assert.IsTrue(
            cookieHeader.Contains("a=1", StringComparison.Ordinal) &&
            cookieHeader.Contains("b=2", StringComparison.Ordinal),
            $"Both cookie values must reach the origin in one header; got: '{cookieHeader}'");
        Assert.IsFalse(cookieHeader.Contains('\n'),
            "The merged Cookie header must not contain a newline.");
    }

    // ---------------------------------------------------------------- BeforeResponse status override

    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_BeforeResponse_CanOverrideStatus()
    {
        // Bug class: BeforeResponse running during the H2→H1.1 bridge must let a handler replace
        // the status code and body the client actually sees.
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("original-body"));

        var (proxy, endpoint) = StartBridgeProxy(suite);
        proxy.BeforeResponse += (_, e) =>
        {
            e.SetResponseBodyString("intercepted-body");
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var (status, body) = await SendH2RequestAsync(raw, 1, "GET", "/");

        Assert.AreEqual("200", status);
        Assert.AreEqual("intercepted-body", body);
    }

    // ---------------------------------------------------------------- Multiple concurrent streams

    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_ConcurrentStreams_AllReceiveTheirResponse()
    {
        // Bug class: each H2 stream must get its own independent H1.1 origin connection and response;
        // body from one stream must not leak into another.
        const int streamCount = 4;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            var path = ctx.Request.Path.ToString();
            return ctx.Response.WriteAsync("resp:" + path);
        });

        var (_, endpoint) = StartBridgeProxy(suite);

        // Open four independent raw connections and fire requests concurrently so the bridge
        // must route each stream in parallel — catching any per-stream isolation bug.
        var tasks = Enumerable.Range(0, streamCount).Select(async i =>
        {
            using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
            return await SendH2RequestAsync(raw, 1, "GET", $"/path{i}");
        }).ToList();

        var results = await Task.WhenAll(tasks);

        for (var i = 0; i < streamCount; i++)
        {
            Assert.AreEqual("200", results[i].Status, $"connection {i} wrong status");
            Assert.AreEqual($"resp:/path{i}", results[i].Body, $"connection {i} body mismatch");
        }
    }

    // ---------------------------------------------------------------- PUT with body

    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_Put_WithBody_RelaysCorrectly()
    {
        // Bug class: PUT body must be relayed exactly once — not replayed on a stale socket.
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        var receivedBody = string.Empty;
        server.HandleRequest(async ctx =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            receivedBody = await sr.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("updated");
        });

        var (_, endpoint) = StartBridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var payload = Encoding.UTF8.GetBytes("{\"value\":42}");
        var (status, body) = await SendH2RequestAsync(raw, 1, "PUT", "/resource/42",
            extraHeaders: [("content-type", "application/json")],
            requestBody: payload);

        Assert.AreEqual("200", status);
        Assert.AreEqual("updated", body);
        Assert.AreEqual("{\"value\":42}", receivedBody);
    }

    // ---------------------------------------------------------------- 404 response

    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_404_Response_IsRelayedCorrectly()
    {
        // Bug class: non-2xx status codes must be passed through; a 200 must not be substituted.
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = 404;
            return ctx.Response.WriteAsync("not-found");
        });

        var (_, endpoint) = StartBridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var (status, body) = await SendH2RequestAsync(raw, 1, "GET", "/missing");
        Assert.AreEqual("404", status);
        Assert.AreEqual("not-found", body);
    }

    // ---------------------------------------------------------------- 500 response

    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_500_Response_IsRelayedWithoutCrashingProxy()
    {
        // Bug class: a 500 from origin must not cause an exception inside the bridge.
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = 500;
            return ctx.Response.WriteAsync("server-error");
        });

        var (_, endpoint) = StartBridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var (status, body) = await SendH2RequestAsync(raw, 1, "GET", "/error");
        Assert.AreEqual("500", status);
        Assert.AreEqual("server-error", body);
    }

    // ---------------------------------------------------------------- BeforeRequest adds a custom request header

    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_BeforeRequest_AddedHeader_ReachesOrigin()
    {
        // Bug class: headers injected by BeforeRequest must arrive at the H1.1 origin.
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        var headerValue = string.Empty;
        server.HandleRequest(ctx =>
        {
            headerValue = ctx.Request.Headers["X-Injected"].ToString();
            return ctx.Response.WriteAsync("ok");
        });

        var (proxy, endpoint) = StartBridgeProxy(suite);
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Headers.AddHeader("X-Injected", "bridge-header");
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var (status, body) = await SendH2RequestAsync(raw, 1, "GET", "/");

        Assert.AreEqual("200", status);
        Assert.AreEqual("ok", body);
        Assert.AreEqual("bridge-header", headerValue,
            "A header injected via BeforeRequest must reach the HTTP/1.1 origin.");
    }

    // ---------------------------------------------------------------- HEAD request — no body path

    /// <summary>
    ///     Bug class: a HEAD response has no body regardless of Content-Length; the bridge
    ///     (<c>Http2ToHttp11BridgeHandler</c>) must not try to read body bytes from the H1.1 origin
    ///     or emit DATA frames. The HEADERS frame sent to the H2 client must carry END_STREAM.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_HEAD_Request_NoBodyFromOrigin_CompletesCleanly()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(context =>
        {
            // ASP.NET Core strips the body for HEAD automatically; the bridge must honour that.
            context.Response.ContentLength = 42;
            return Task.CompletedTask;
        });

        var (proxy, endpoint) = StartBridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        // Send HEAD request on stream 1.
        var headers = raw.Connection.EncodeHeaders(
            [(":method", "HEAD"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, headers, endStream: true);

        // The response HEADERS frame must have END_STREAM set — no DATA frames allowed for HEAD.
        var (respHeaders, endStream, errorCode) =
            await raw.Connection.ReadHeadersOrRstAsync(1).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsNotNull(respHeaders, $"HEAD response must arrive (error: {errorCode}).");
        Assert.AreEqual(Http2ErrorCode.NoError, errorCode);
        Assert.IsTrue(endStream,
            "HEAD response HEADERS frame must carry END_STREAM — no body should follow.");
        var status = respHeaders!.Single(h => h.Name == ":status").Value;
        Assert.AreEqual("200", status);
    }

    // ---------------------------------------------------------------- chunked origin response — LimitedStream path

    /// <summary>
    ///     Bug class: when the HTTP/1.1 origin sends <c>Transfer-Encoding: chunked</c> (no Content-Length),
    ///     the bridge must read the body via <c>LimitedStream</c> and relay it as H2 DATA frames rather
    ///     than treating it as a zero-length response.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_ChunkedOriginResponse_RelayedAsH2DataFrames()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async context =>
        {
            // Writing without setting Content-Length forces ASP.NET Core / Kestrel to use
            // Transfer-Encoding: chunked on the HTTP/1.1 origin connection.
            await context.Response.WriteAsync("part-one");
            await context.Response.Body.FlushAsync();
            await context.Response.WriteAsync("-part-two");
        });

        var (proxy, endpoint) = StartBridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var (status, body) = await SendH2RequestAsync(raw, 1, "GET", "/chunked");

        Assert.AreEqual("200", status);
        Assert.AreEqual("part-one-part-two", body,
            "Chunked origin body must be fully relayed as H2 DATA frames.");
    }
}
