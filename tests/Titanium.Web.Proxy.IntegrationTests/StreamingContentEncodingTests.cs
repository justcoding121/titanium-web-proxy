#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     End-to-end coverage for decompress → optional edit → recompress across
///     <c>OnResponseBodyWrite</c> / <c>OnRequestBodyWrite</c> for HTTP/1.1, HTTP/2, and HTTP/3.
///     Hooks see wire bytes; BCL <see cref="GZipStream"/> decompress is pull-based, so these tests
///     buffer until <c>IsLastChunk</c> then emit one recompressed payload (matching the public sample).
/// </summary>
[DoNotParallelize]
[TestClass]
public class StreamingContentEncodingTests
{
    private const string Needle = "http://example.invalid/path";
    private const string Replacement = "https://example.invalid/path";

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

    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Http11_Gzip_DecompressEditRecompress()
    {
        await RunHttp11OrHttp2ResponseGzipTransformAsync(http2: false);
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Http2_Gzip_DecompressEditRecompress()
    {
        await RunHttp11OrHttp2ResponseGzipTransformAsync(http2: true);
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Http3_Gzip_DecompressEditRecompress()
    {
        RequireQuic();

        var plain = BuildPlainPayload();
        var gzipped = GzipCompress(plain);
        var expectedPlain = ApplyEdit(plain);

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "gzip", gzipped,
            extraHeaders: new List<(string, string)> { ("content-encoding", "gzip") },
            dataFrameSize: 64)));

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        var hookCount = 0;
        AttachGzipResponseTransform(proxy, () => Interlocked.Increment(ref hookCount));

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/gzip-rw");

        Assert.AreEqual(200, response.StatusCode);
        Assert.IsTrue(hookCount > 1, "Multi-frame gzip response should invoke the body-write hook more than once.");
        CollectionAssert.AreEqual(expectedPlain, GzipDecompress(response.Body));
    }

    [TestMethod]
    public async Task OnRequestBodyWrite_Http11_Gzip_DecompressEditRecompress()
    {
        await RunHttp11OrHttp2RequestGzipTransformAsync(http2: false);
    }

    [TestMethod]
    public async Task OnRequestBodyWrite_Http2_Gzip_DecompressEditRecompress()
    {
        await RunHttp11OrHttp2RequestGzipTransformAsync(http2: true);
    }

    [TestMethod]
    public async Task OnRequestBodyWrite_Http3_Gzip_DecompressEditRecompress()
    {
        RequireQuic();

        var plain = BuildPlainPayload();
        var gzipped = GzipCompress(plain);
        var expectedPlain = ApplyEdit(plain);
        byte[]? originWire = null;

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            originWire = req.Body;
            return Task.FromResult(new QuicHttp3Response(200, $"len={req.Body.Length}"));
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        var hookCount = 0;
        AttachGzipRequestTransform(proxy, () => Interlocked.Increment(ref hookCount));

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        var response = await client.SendAsync("POST", $"localhost:{origin.Port}", "/gzip-post", gzipped,
            requestDataFrameSize: 64,
            extraRequestHeaders: new List<(string, string)> { ("content-encoding", "gzip") });

        Assert.AreEqual(200, response.StatusCode);
        Assert.IsNotNull(originWire);
        Assert.IsTrue(hookCount > 1, "Multi-frame gzip request should invoke the body-write hook more than once.");
        CollectionAssert.AreEqual(expectedPlain, GzipDecompress(originWire));
    }

    private static async Task RunHttp11OrHttp2ResponseGzipTransformAsync(bool http2)
    {
        using var testSuite = new TestSuite(sharedServer);

        var plain = BuildPlainPayload();
        var gzipped = GzipCompress(plain);
        var expectedPlain = ApplyEdit(plain);

        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            context.Response.Headers.ContentEncoding = "gzip";
            context.Response.ContentLength = gzipped.Length;
            // Small writes so HTTP/2 origin DATA frames (and H1 buffer pieces for large bodies) stream.
            const int piece = 1024;
            for (var offset = 0; offset < gzipped.Length; offset += piece)
            {
                var len = Math.Min(piece, gzipped.Length - offset);
                await context.Response.Body.WriteAsync(gzipped.AsMemory(offset, len));
                await context.Response.Body.FlushAsync();
            }
        });

        var proxy = testSuite.GetProxy();
        if (http2) proxy.EnableHttp2 = true;

        var hookCount = 0;
        AttachGzipResponseTransform(proxy, () => Interlocked.Increment(ref hookCount));

        using var client = http2 ? CreateHttp2Client(proxy) : testSuite.GetClient(proxy);
        // No AutomaticDecompression — assert Content-Encoding survives and body is still gzip.
        var url = http2 ? server.ListeningHttpsUrl : server.ListeningHttpUrl;
        var response = await client.GetAsync(new Uri(url));
        var wire = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        if (http2)
            Assert.AreEqual(new Version(2, 0), response.Version);

        Assert.IsTrue(response.Content.Headers.ContentEncoding.Contains("gzip"),
            "Client should still see Content-Encoding: gzip after recompress.");
        Assert.IsTrue(hookCount > 0, "OnResponseBodyWrite should fire for the gzip body.");
        CollectionAssert.AreEqual(expectedPlain, GzipDecompress(wire));
    }

    private static async Task RunHttp11OrHttp2RequestGzipTransformAsync(bool http2)
    {
        using var testSuite = new TestSuite(sharedServer);

        var plain = BuildPlainPayload();
        var gzipped = GzipCompress(plain);
        var expectedPlain = ApplyEdit(plain);
        byte[]? originWire = null;

        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            originWire = ms.ToArray();
            await context.Response.WriteAsync($"len={originWire.Length}");
        });

        var proxy = testSuite.GetProxy();
        if (http2) proxy.EnableHttp2 = true;

        var hookCount = 0;
        AttachGzipRequestTransform(proxy, () => Interlocked.Increment(ref hookCount));

        using var client = http2 ? CreateHttp2Client(proxy) : testSuite.GetClient(proxy);
        var url = http2 ? server.ListeningHttpsUrl : server.ListeningHttpUrl;

        using var content = new ByteArrayContent(gzipped);
        content.Headers.ContentEncoding.Add("gzip");
        content.Headers.ContentLength = gzipped.Length;

        var response = await client.PostAsync(new Uri(url), content);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        if (http2)
            Assert.AreEqual(new Version(2, 0), response.Version);

        Assert.IsNotNull(originWire);
        Assert.IsTrue(hookCount > 0, "OnRequestBodyWrite should fire for the gzip body.");
        Assert.IsTrue(responseBody.StartsWith("len=", StringComparison.Ordinal));
        CollectionAssert.AreEqual(expectedPlain, GzipDecompress(originWire));
    }

    private static void AttachGzipResponseTransform(ProxyServer proxy, Action onHook)
    {
        proxy.BeforeResponse += (_, e) =>
        {
            var ce = e.HttpClient.Response.ContentEncoding;
            if (ce == null || !ce.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            // Recompressed size will not match origin Content-Length.
            e.HttpClient.Response.Headers.RemoveHeader(KnownHeaders.ContentLength);
            if (e.HttpClient.Request.HttpVersion.Major < 2)
                e.HttpClient.Response.IsChunked = true;

            e.UserData = new MemoryStream();
            return Task.CompletedTask;
        };

        proxy.OnResponseBodyWrite += (_, e) =>
        {
            onHook();
            return TransformGzipWireAsync(e);
        };
    }

    private static void AttachGzipRequestTransform(ProxyServer proxy, Action onHook)
    {
        proxy.BeforeRequest += (_, e) =>
        {
            var ce = e.HttpClient.Request.ContentEncoding;
            if (ce == null || !ce.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            e.HttpClient.Request.Headers.RemoveHeader(KnownHeaders.ContentLength);
            if (e.HttpClient.Request.HttpVersion.Major < 2)
                e.HttpClient.Request.IsChunked = true;

            e.UserData = new MemoryStream();
            return Task.CompletedTask;
        };

        proxy.OnRequestBodyWrite += (_, e) =>
        {
            onHook();
            return TransformGzipWireAsync(e);
        };
    }

    private static Task TransformGzipWireAsync(BeforeBodyWriteEventArgs e)
    {
        if (e.Session.UserData is not MemoryStream wire)
            return Task.CompletedTask;

        if (e.BodyBytes is { Length: > 0 })
            wire.Write(e.BodyBytes, 0, e.BodyBytes.Length);

        if (!e.IsLastChunk)
        {
            e.BodyBytes = Array.Empty<byte>();
            return Task.CompletedTask;
        }

        wire.Position = 0;
        var plain = GzipDecompress(wire);
        wire.Dispose();
        e.Session.UserData = null;

        plain = ApplyEdit(plain);
        e.BodyBytes = GzipCompress(plain);
        return Task.CompletedTask;
    }

    private static byte[] BuildPlainPayload()
    {
        // Low-compressibility prefix so gzip wire size exceeds the default 8 KiB body buffer
        // (HTTP/1.x hook pieces) while still containing an editable ASCII needle.
        var prefix = new byte[12 * 1024];
        for (var i = 0; i < prefix.Length; i++)
            prefix[i] = (byte)(31 + (i * 17) % 95);

        var needle = Encoding.UTF8.GetBytes(Needle);
        var payload = new byte[prefix.Length + needle.Length + 64];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(needle, 0, payload, prefix.Length, needle.Length);
        for (var i = prefix.Length + needle.Length; i < payload.Length; i++)
            payload[i] = (byte)('A' + (i % 26));
        return payload;
    }

    private static byte[] ApplyEdit(byte[] plain)
    {
        var text = Encoding.UTF8.GetString(plain);
        Assert.IsTrue(text.Contains(Needle, StringComparison.Ordinal),
            "Test payload must contain the needle before edit.");
        return Encoding.UTF8.GetBytes(text.Replace(Needle, Replacement, StringComparison.Ordinal));
    }

    private static byte[] GzipCompress(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(plain, 0, plain.Length);
        return ms.ToArray();
    }

    private static byte[] GzipDecompress(byte[] gzipped)
    {
        using var input = new MemoryStream(gzipped);
        return GzipDecompress(input);
    }

    private static byte[] GzipDecompress(Stream gzipped)
    {
        using var gzip = new GZipStream(gzipped, CompressionMode.Decompress, leaveOpen: true);
        using var plain = new MemoryStream();
        gzip.CopyTo(plain);
        return plain.ToArray();
    }

    private static ProxyServer CreateHttp3Proxy(TransparentQuicProxyEndPoint quicEndPoint)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        quicEndPoint.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };

        proxy.AddEndPoint(quicEndPoint);
        proxy.Start();
        return proxy;
    }

    private static HttpClient CreateHttp2Client(ProxyServer proxy)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new TestHelper.TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions =
            {
                RemoteCertificateValidationCallback =
                    (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
            }
        };

        return new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
