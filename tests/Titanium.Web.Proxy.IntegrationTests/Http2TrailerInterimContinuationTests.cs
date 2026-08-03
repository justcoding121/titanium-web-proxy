using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for HTTP/2 behavior that cannot be driven through a real HttpClient on both ends
///     of the proxy at once: response and request trailers (RFC 7540 §8.1.2.1), interim (1xx) informational
///     responses relayed over h2 (RFC 9110 §15.2), and HEADERS/CONTINUATION reassembly + re-splitting (RFC
///     7540 §4.3/§6.10). HttpClient has no public API to send request trailers or to deliberately fragment
///     a header block across CONTINUATION frames, and does not reliably surface informational responses to
///     test code - so these tests use <see cref="Http2RawClient" /> and <see cref="Http2RawOriginServer" />
///     (hand-rolled but protocol-accurate h2 endpoints built on the proxy's own internal frame/HPACK types)
///     on both sides, while still routing every request through a completely real <see cref="ProxyServer" />.
///     Complements the HPACK dynamic-table-reuse coverage in <see cref="Http2Tests" /> and the trailer/interim
///     coverage already in <see cref="ChunkedTrailerTests" />/<see cref="InterimResponseTests" /> for HTTP/1.x.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2TrailerInterimContinuationTests
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

    private static X509Certificate2 CreateOriginCertificate()
    {
        return TestCertificateAuthority.ServerCertificate;
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Response_Trailers_From_Origin_Are_Relayed_To_Client()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var headers = connection.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, false);

            var body = Encoding.ASCII.GetBytes("hello");
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, 0, body);

            var trailers = connection.EncodeHeaders(Array.Empty<(string, string)>(),
                new[] { ("x-trailer", "trailer-value") });
            await connection.WriteHeaderBlockAsync(streamId, trailers, true);
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, mainResponse, mainEndStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", mainResponse.Single(h => h.Name == ":status").Value);
        Assert.IsFalse(mainEndStream);

        var dataFrame = await rawClient.Connection.ReadFrameAsync();
        Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
        Assert.AreEqual("hello", Encoding.ASCII.GetString(dataFrame.Payload));
        Assert.IsFalse((dataFrame.Flags & Http2FrameFlag.EndStream) != 0);

        var (_, trailerHeaders, trailerEndStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.IsTrue(trailerEndStream, "The trailer HEADERS block should have carried END_STREAM.");
        Assert.AreEqual("trailer-value", trailerHeaders.Single(h => h.Name == "x-trailer").Value);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Request_Trailers_From_RawClient_Are_Relayed_To_Origin()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());

        List<(string Name, string Value)>? receivedTrailers = null;
        var trailersReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();

            var (streamId, _, requestEndStream) = await connection.ReadHeaderBlockAsync();
            Assert.IsFalse(requestEndStream, "The main request HEADERS should not have carried END_STREAM.");

            while (true)
            {
                var frame = await connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data)
                {
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    {
                        break;
                    }
                }
                else if (frame.Type == Http2FrameType.Headers && frame.StreamId == streamId)
                {
                    receivedTrailers = connection.DecodeHeaders(frame.Payload);
                    break;
                }
            }

            trailersReceived.TrySetResult(true);

            var responseHeaders = connection.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, responseHeaders, true);
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient =
            await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "POST"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);

        var requestBody = Encoding.ASCII.GetBytes("request-body");
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, requestBody);

        var requestTrailers = rawClient.Connection.EncodeHeaders(Array.Empty<(string, string)>(),
            new[] { ("x-request-trailer", "req-trailer-value") });
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestTrailers, true);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        await Task.WhenAny(trailersReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(trailersReceived.Task.IsCompleted,
            "The origin server never received the client's trailer HEADERS block.");
        Assert.IsNotNull(receivedTrailers);
        Assert.AreEqual("req-trailer-value",
            receivedTrailers.Single(h => h.Name == "x-request-trailer").Value);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Interim_1xx_Response_Is_Relayed_Before_Final_Response()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var interimHeaders = connection.EncodeHeaders(new[] { (":status", "103") },
                new[] { ("link", "</style.css>; rel=preload") });
            await connection.WriteHeaderBlockAsync(streamId, interimHeaders, false);

            var finalHeaders = connection.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, finalHeaders, false);

            var body = Encoding.ASCII.GetBytes("final-body");
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, body);
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient =
            await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, interimResponse, interimEndStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("103", interimResponse.Single(h => h.Name == ":status").Value);
        Assert.AreEqual("</style.css>; rel=preload", interimResponse.Single(h => h.Name == "link").Value);
        Assert.IsFalse(interimEndStream, "An interim response must never end the stream.");

        var (_, finalResponse, finalEndStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", finalResponse.Single(h => h.Name == ":status").Value);
        Assert.IsFalse(finalEndStream);

        var dataFrame = await rawClient.Connection.ReadFrameAsync();
        Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
        Assert.AreEqual("final-body", Encoding.ASCII.GetString(dataFrame.Payload));
        Assert.IsTrue((dataFrame.Flags & Http2FrameFlag.EndStream) != 0);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Large_Response_Header_Split_Across_Continuation_Is_Reassembled_And_Relayed()
    {
        // Comfortably under the decoder's fixed 8192-byte max-decompressed-header-block size (see
        // Http2Helper.CopyHttp2FrameAsync's `new Decoder(8192, ...)`) - large enough that, combined with
        // forcing the origin to fragment it at a small frame size below, the proxy's HEADERS/CONTINUATION
        // reassembly path is exercised on the way in, but small enough it is not silently truncated by
        // that unrelated, pre-existing size cap.
        var largeValue = string.Concat(Enumerable.Range(0, 190).Select(_ => Guid.NewGuid().ToString("N")));

        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var headers = connection.EncodeHeaders(new[] { (":status", "200") },
                new[] { ("x-large", largeValue) });

            // Force fragmentation at the origin regardless of the actual encoded size.
            await connection.WriteHeaderBlockAsync(streamId, headers, true, 1024);
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        Assert.IsTrue(endStream);
        Assert.AreEqual(largeValue, responseHeaders.Single(h => h.Name == "x-large").Value,
            "The large header split across CONTINUATION frames by the origin was not relayed intact.");
    }
}
