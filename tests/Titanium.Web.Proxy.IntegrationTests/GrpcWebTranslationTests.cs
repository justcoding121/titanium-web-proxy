using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Grpc;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Route <c>grpcWeb: true</c> against a hand-rolled HTTP/2 gRPC origin, from an HTTP/2 client (the
///     per-stream dispatch bridge) and from an HTTP/1.1 client (the HTTP/1.1-to-HTTP/2 bridge).
/// </summary>
[TestClass]
[DoNotParallelize]
public class GrpcWebTranslationTests
{
    private static readonly byte[] RequestMessage = [0x0a, 0x03, (byte)'a', (byte)'b', (byte)'c'];
    private static readonly byte[] ReplyOne = [0x0a, 0x01, (byte)'1'];
    private static readonly byte[] ReplyTwo = [0x0a, 0x01, (byte)'2'];
    private static readonly byte[] ReplyThree = [0x0a, 0x01, (byte)'3'];

    /// <summary>
    ///     The first server-certificate handshakes of a fresh process can fail on some Windows hosts
    ///     ("unknown chain building error"). Send one throwaway request through the whole stack, ignoring the
    ///     outcome, so no test eats that failure.
    /// </summary>
    [ClassInitialize]
    public static async Task WarmUpTlsAsync(TestContext context)
    {
        _ = context;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var origin = CreateOrigin(out _);
                await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: false);
                using var client = CreateClient(proxy, HttpVersion.Version20);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var request = GrpcWebRequest(proxy, "/pkg.Svc/Unary", "application/grpc");
                using var response = await client.SendAsync(request, cts.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                // Expected on the flaky first handshake; try again.
            }
        }
    }
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_Unary_TranslatesRequestAndEndsWithATrailerFrame()
    {
        using var origin = CreateOrigin(out var seen);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: true);
        using var client = CreateClient(proxy, HttpVersion.Version20);

        using var response = await client.SendAsync(GrpcWebRequest(proxy, "/pkg.Svc/Unary", "application/grpc-web+proto"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/grpc-web+proto", response.Content.Headers.ContentType?.ToString());
        AssertUnaryBody(body);
        Assert.AreEqual((long)body.Length, response.Content.Headers.ContentLength);
        var request = seen.Single();
        Assert.AreEqual("application/grpc", request.ContentType);
        Assert.AreEqual("trailers", request.Te);
        CollectionAssert.AreEqual(GrpcWebAdapter.EncodeFrame(RequestMessage), request.Body);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_UnaryWithoutProtoSuffix_IsAnsweredWithTheSameMediaType()
    {
        using var origin = CreateOrigin(out _);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: true);
        using var client = CreateClient(proxy, HttpVersion.Version20);

        using var response = await client.SendAsync(GrpcWebRequest(proxy, "/pkg.Svc/Unary", "application/grpc-web"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/grpc-web", response.Content.Headers.ContentType?.ToString());
        AssertUnaryBody(body);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_ServerStreaming_RelaysEveryMessageThenTheTrailerFrame()
    {
        using var origin = CreateOrigin(out _);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: true);
        using var client = CreateClient(proxy, HttpVersion.Version20);

        using var response = await client.SendAsync(GrpcWebRequest(proxy, "/pkg.Svc/Stream", "application/grpc-web+proto"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual("application/grpc-web+proto", response.Content.Headers.ContentType?.ToString());
        AssertStreamBody(body);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_TrailersOnlyError_BecomesATrailerFrameAndKeepsTheHeaders()
    {
        using var origin = CreateOrigin(out _);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: true);
        using var client = CreateClient(proxy, HttpVersion.Version20);

        using var response = await client.SendAsync(GrpcWebRequest(proxy, "/pkg.Svc/Missing", "application/grpc-web+proto"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/grpc-web+proto", response.Content.Headers.ContentType?.ToString());
        Assert.AreEqual("12", response.Headers.GetValues("grpc-status").Single());
        var (messages, trailer) = ReadFrames(body);
        Assert.AreEqual(0, messages.Count);
        StringAssert.Contains(trailer, "grpc-status: 12\r\n");
        StringAssert.Contains(trailer, "grpc-message: not implemented\r\n");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_NonGrpcOriginAnswer_IsLeftAlone()
    {
        using var origin = CreateOrigin(out _);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: true);
        using var client = CreateClient(proxy, HttpVersion.Version20);

        using var response = await client.SendAsync(GrpcWebRequest(proxy, "/pkg.Svc/Html", "application/grpc-web+proto"));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("no such method", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_RouteWithoutTheFlag_PassesGrpcWebThroughUntouched()
    {
        using var origin = CreateOrigin(out var seen);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: false);
        using var client = CreateClient(proxy, HttpVersion.Version20);

        using var response = await client.SendAsync(GrpcWebRequest(proxy, "/pkg.Svc/Unary", "application/grpc-web+proto"));
        _ = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual("application/grpc-web+proto", seen.Single().ContentType);
        Assert.IsNull(seen.Single().Te);
        Assert.AreEqual("application/grpc", response.Content.Headers.ContentType?.ToString());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_TextVariant_IsNotTranslated()
    {
        using var origin = CreateOrigin(out var seen);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: true);
        using var client = CreateClient(proxy, HttpVersion.Version20);

        var request = GrpcWebRequest(proxy, "/pkg.Svc/Unary", "application/grpc-web-text");
        request.Content = new StringContent(Convert.ToBase64String(GrpcWebAdapter.EncodeFrame(RequestMessage)),
            Encoding.ASCII, "application/grpc-web-text");
        using var response = await client.SendAsync(request);
        _ = await response.Content.ReadAsByteArrayAsync();

        Assert.IsTrue(seen.Single().ContentType!.StartsWith("application/grpc-web-text", StringComparison.Ordinal));
        Assert.AreEqual("application/grpc", response.Content.Headers.ContentType?.ToString());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1Client_H2Origin_Unary_TranslatesRequestAndEndsWithATrailerFrame()
    {
        using var origin = CreateOrigin(out var seen);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: true, translateToH2: true);
        using var client = CreateClient(proxy, HttpVersion.Version11);

        using var response = await client.SendAsync(GrpcWebRequest(proxy, "/pkg.Svc/Unary", "application/grpc-web+proto", HttpVersion.Version11));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(HttpVersion.Version11, response.Version);
        Assert.AreEqual("application/grpc-web+proto", response.Content.Headers.ContentType?.ToString());
        AssertUnaryBody(body);
        Assert.AreEqual("0", response.Headers.GetValues("grpc-status").Single());
        var request = seen.Single();
        Assert.AreEqual("application/grpc", request.ContentType);
        Assert.AreEqual("trailers", request.Te);
        CollectionAssert.AreEqual(GrpcWebAdapter.EncodeFrame(RequestMessage), request.Body);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1Client_H2Origin_ServerStreaming_RelaysEveryMessageThenTheTrailerFrame()
    {
        using var origin = CreateOrigin(out _);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: true, translateToH2: true);
        using var client = CreateClient(proxy, HttpVersion.Version11);

        using var response = await client.SendAsync(GrpcWebRequest(proxy, "/pkg.Svc/Stream", "application/grpc-web+proto", HttpVersion.Version11));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpVersion.Version11, response.Version);
        Assert.AreEqual("application/grpc-web+proto", response.Content.Headers.ContentType?.ToString());
        AssertStreamBody(body);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1Client_H2Origin_TrailersOnlyError_BecomesATrailerFrame()
    {
        using var origin = CreateOrigin(out _);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: true, translateToH2: true);
        using var client = CreateClient(proxy, HttpVersion.Version11);

        using var response = await client.SendAsync(GrpcWebRequest(proxy, "/pkg.Svc/Missing", "application/grpc-web+proto", HttpVersion.Version11));
        var body = await response.Content.ReadAsByteArrayAsync();

        var (messages, trailer) = ReadFrames(body);
        Assert.AreEqual(0, messages.Count);
        StringAssert.Contains(trailer, "grpc-status: 12\r\n");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1Client_H2Origin_RouteWithoutTheFlag_PassesGrpcWebThroughUntouched()
    {
        using var origin = CreateOrigin(out var seen);
        await using var proxy = await StartProxyAsync(origin.Port, grpcWeb: false, translateToH2: true);
        using var client = CreateClient(proxy, HttpVersion.Version11);

        using var response = await client.SendAsync(GrpcWebRequest(proxy, "/pkg.Svc/Unary", "application/grpc-web+proto", HttpVersion.Version11));
        _ = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual("application/grpc-web+proto", seen.Single().ContentType);
        Assert.AreEqual("application/grpc", response.Content.Headers.ContentType?.ToString());
    }

    private static void AssertUnaryBody(byte[] body)
    {
        var (messages, trailer) = ReadFrames(body);
        Assert.AreEqual(1, messages.Count);
        CollectionAssert.AreEqual(ReplyOne, messages[0]);
        Assert.AreEqual("grpc-status: 0\r\ngrpc-message: fine\r\n", trailer);
    }

    private static void AssertStreamBody(byte[] body)
    {
        var (messages, trailer) = ReadFrames(body);
        Assert.AreEqual(3, messages.Count);
        CollectionAssert.AreEqual(ReplyOne, messages[0]);
        CollectionAssert.AreEqual(ReplyTwo, messages[1]);
        CollectionAssert.AreEqual(ReplyThree, messages[2]);
        Assert.AreEqual("grpc-status: 0\r\ngrpc-message: done\r\n", trailer);
    }

    /// <summary>Splits a gRPC-Web body into data messages and the text of its one trailer frame (flag 0x80).</summary>
    private static (List<byte[]> Messages, string Trailer) ReadFrames(byte[] body)
    {
        var messages = new List<byte[]>();
        string? trailer = null;
        var offset = 0;
        while (offset < body.Length)
        {
            Assert.IsNull(trailer, "Nothing may follow the trailer frame.");
            Assert.IsTrue(GrpcWebAdapter.TryReadFrame(body.AsSpan(offset), out _, out var payload, out var consumed),
                "Body ends inside a frame.");
            if ((body[offset] & GrpcWebAdapter.TrailerFrameFlag) != 0)
                trailer = Encoding.UTF8.GetString(payload);
            else
                messages.Add(payload.ToArray());
            offset += consumed;
        }

        Assert.IsNotNull(trailer, "The body has no trailer frame.");
        return (messages, trailer);
    }

    private static HttpRequestMessage GrpcWebRequest(Started proxy, string path, string contentType, Version? version = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{proxy.Tls.Port}{path}")
        {
            Version = version ?? HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(GrpcWebAdapter.EncodeFrame(RequestMessage)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Headers.Add("x-grpc-web", "1");
        return request;
    }

    private sealed record SeenRequest(string Path, string? ContentType, string? Te, byte[] Body);

    /// <summary>A gRPC-shaped HTTP/2 origin: replies by path, and sends status in trailers (or in headers for a trailers-only reply).</summary>
    private static Http2RawOriginServer CreateOrigin(out ConcurrentQueue<SeenRequest> seen)
    {
        var requests = new ConcurrentQueue<SeenRequest>();
        seen = requests;
        var server = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        server.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, body) = await connection.ReadRequestAsync();
                string? Header(string name) => headers.FirstOrDefault(h => h.Name == name).Value;
                var path = Header(":path")!;
                requests.Enqueue(new SeenRequest(path, Header("content-type"), Header("te"), body));

                switch (path)
                {
                    case "/pkg.Svc/Unary":
                        await WriteUnaryAsync(connection, streamId);
                        break;
                    case "/pkg.Svc/Stream":
                        await WriteStreamAsync(connection, streamId);
                        break;
                    case "/pkg.Svc/Missing":
                        await WriteHeadersAsync(connection, streamId, endStream: true,
                            ("content-type", "application/grpc"), ("grpc-status", "12"),
                            ("grpc-message", "not implemented"));
                        break;
                    default:
                        var text = Encoding.UTF8.GetBytes("no such method");
                        await WriteHeadersAsync(connection, streamId, endStream: false, status: "404",
                            ("content-type", "text/plain"), ("content-length", text.Length.ToString()));
                        await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, text);
                        break;
                }
            }
        });
        return server;
    }

    private static async Task WriteHeadersAsync(Http2RawFrame.Connection connection, int streamId, bool endStream,
        params (string Name, string Value)[] headers) =>
        await WriteHeadersAsync(connection, streamId, endStream, "200", headers);

    private static async Task WriteHeadersAsync(Http2RawFrame.Connection connection, int streamId, bool endStream,
        string status, params (string Name, string Value)[] headers)
    {
        var block = connection.EncodeHeaders([(":status", status)], headers);
        await connection.WriteHeaderBlockAsync(streamId, block, endStream);
    }

    private static async Task WriteTrailersAsync(Http2RawFrame.Connection connection, int streamId, string message)
    {
        var block = connection.EncodeHeaders(Array.Empty<(string, string)>(),
            [("grpc-status", "0"), ("grpc-message", message)]);
        await connection.WriteHeaderBlockAsync(streamId, block, endStream: true);
    }

    /// <summary>A known-length reply: the proxy buffers it, so the trailers are in hand before the headers go out.</summary>
    private static async Task WriteUnaryAsync(Http2RawFrame.Connection connection, int streamId)
    {
        var message = GrpcWebAdapter.EncodeFrame(ReplyOne);
        await WriteHeadersAsync(connection, streamId, endStream: false,
            ("content-type", "application/grpc"), ("content-length", message.Length.ToString()));
        await connection.WriteFrameAsync(Http2FrameType.Data, streamId, 0, message);
        await WriteTrailersAsync(connection, streamId, "fine");
    }

    /// <summary>An open-ended reply: the proxy streams it, and the status trails the last message.</summary>
    private static async Task WriteStreamAsync(Http2RawFrame.Connection connection, int streamId)
    {
        await WriteHeadersAsync(connection, streamId, endStream: false, ("content-type", "application/grpc"));
        foreach (var reply in new[] { ReplyOne, ReplyTwo, ReplyThree })
        {
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, 0, GrpcWebAdapter.EncodeFrame(reply));
            await Task.Delay(50);
        }

        await WriteTrailersAsync(connection, streamId, "done");
    }

    private static async Task<Started> StartProxyAsync(int originPort, bool grpcWeb, bool translateToH2 = false)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "c1-d0", Address = "127.0.0.1", Port = originPort }],
            },
        ]);

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    GrpcWeb = grpcWeb,
                },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
        };

        var tls = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = false,
        };
        if (translateToH2)
        {
            tls.BeforeSslAuthenticate += (_, e) =>
            {
                e.DecryptSsl = true;
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };
        }

        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxy.AddEndPoint(tls);
        proxy.Start();
        return new Started(proxy, tls);
    }

    private static HttpClient CreateClient(Started proxy, Version version)
    {
        _ = proxy;
        var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null },
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    private sealed class Started : IAsyncDisposable
    {
        public Started(ProxyServer server, TransparentProxyEndPoint tls)
        {
            Server = server;
            Tls = tls;
        }

        public ProxyServer Server { get; }

        public TransparentProxyEndPoint Tls { get; }

        public async ValueTask DisposeAsync()
        {
            if (Server.ProxyRunning)
                await Server.StopAsync();
            Server.Dispose();
        }
    }
}
