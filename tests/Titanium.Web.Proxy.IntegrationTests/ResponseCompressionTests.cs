using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using static Titanium.Web.Proxy.IntegrationTests.ProtocolMatrixTests;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Buffered response compression through a real proxy: an HTTP/1.1 client (cleartext and TLS) and an
///     HTTP/2 client (the HTTP/2-to-HTTP/1.1 bridge), each against an HTTP/1.1 origin.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ResponseCompressionIntegrationTests
{
    [ClassInitialize]
    public static void WarmUpChainBuilding(TestContext _)
    {
        // On some hosts the first X509Chain.Build in a process throws before the platform chain cache is
        // primed, which fails whichever TLS test runs first. Prime it here; the outcome is irrelevant.
        try
        {
            using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
            chain.Build(Titanium.Web.Proxy.IntegrationTests.Setup.TestCertificateAuthority.ServerCertificate);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Expected on the hosts described above.
        }
    }

    private static readonly byte[] Html = Encoding.ASCII.GetBytes(
        string.Concat(Enumerable.Repeat("<p>compress me, compress me</p>\n", 160)));

    private static RouteConfig[] Routes(string? compression = null) =>
    [
        new RouteConfig
        {
            Id = "r",
            ClusterId = "c1",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            Compression = compression,
        },
    ];

    private static (string, string)[] Accept(string value) => [("Accept-Encoding", value)];

    private static byte[] Decode(string encoding, byte[] wire)
    {
        using var input = new MemoryStream(wire);
        using Stream decoder = encoding == "br"
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static ResponseCompressionConfig On() => new() { Enabled = true };

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task Brotli_IsPreferred_AndRoundTrips(ClientKind kind)
    {
        await using var origin = new HtmlOrigin(Html, etag: "\"v1\"", vary: "Origin");
        await using var proxy = await StartAsync(origin.Port, Routes(), kind);
        proxy.Server.ResponseCompression = On();

        using var response = await SendAsync(proxy, kind, "/page", Accept("gzip, br"));
        var wire = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new[] { "br" }, response.Content.Headers.ContentEncoding.ToArray());
        Assert.IsTrue(wire.Length < Html.Length);
        CollectionAssert.AreEqual(Html, Decode("br", wire));
        if (response.Content.Headers.ContentLength is { } length)
        {
            Assert.AreEqual(wire.Length, length);
        }

        var vary = string.Join(",", response.Headers.Vary);
        StringAssert.Contains(vary, "Accept-Encoding");
        StringAssert.Contains(vary, "Origin");
        Assert.AreEqual("W/\"v1\"", response.Headers.ETag?.ToString());
        Assert.AreEqual(1, origin.Hits);
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task GzipOnlyClient_GetsGzip(ClientKind kind)
    {
        await using var origin = new HtmlOrigin(Html);
        await using var proxy = await StartAsync(origin.Port, Routes(), kind);
        proxy.Server.ResponseCompression = On();

        using var response = await SendAsync(proxy, kind, "/", Accept("gzip"));
        var wire = await response.Content.ReadAsByteArrayAsync();

        CollectionAssert.AreEqual(new[] { "gzip" }, response.Content.Headers.ContentEncoding.ToArray());
        CollectionAssert.AreEqual(Html, Decode("gzip", wire));
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task OffByDefault_LeavesTheResponseAlone(ClientKind kind)
    {
        await using var origin = new HtmlOrigin(Html, etag: "\"v1\"");
        await using var proxy = await StartAsync(origin.Port, Routes(), kind);

        using var response = await SendAsync(proxy, kind, "/", Accept("br, gzip"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(0, response.Content.Headers.ContentEncoding.Count);
        Assert.IsFalse(response.Headers.Vary.Contains("Accept-Encoding"));
        Assert.AreEqual("\"v1\"", response.Headers.ETag?.ToString());
        CollectionAssert.AreEqual(Html, body);
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task RouteCompressionOff_SkipsTheRoute(ClientKind kind)
    {
        await using var origin = new HtmlOrigin(Html);
        await using var proxy = await StartAsync(origin.Port, Routes("off"), kind);
        proxy.Server.ResponseCompression = On();

        using var response = await SendAsync(proxy, kind, "/", Accept("br, gzip"));

        Assert.AreEqual(0, response.Content.Headers.ContentEncoding.Count);
        CollectionAssert.AreEqual(Html, await response.Content.ReadAsByteArrayAsync());
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext, "")]
    [DataRow(ClientKind.H1Cleartext, "identity")]
    [DataRow(ClientKind.H2Tls, "")]
    [Timeout(30_000)]
    public async Task ClientThatDoesNotAcceptAnEncoding_GetsIdentity(ClientKind kind, string accept)
    {
        await using var origin = new HtmlOrigin(Html);
        await using var proxy = await StartAsync(origin.Port, Routes(), kind);
        proxy.Server.ResponseCompression = On();

        using var response = await SendAsync(proxy, kind, "/", accept.Length == 0 ? null : Accept(accept));

        Assert.AreEqual(0, response.Content.Headers.ContentEncoding.Count);
        CollectionAssert.AreEqual(Html, await response.Content.ReadAsByteArrayAsync());
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task BodyBelowMinBytes_IsNotCompressed(ClientKind kind)
    {
        var small = Encoding.ASCII.GetBytes(new string('a', 512));
        await using var origin = new HtmlOrigin(small);
        await using var proxy = await StartAsync(origin.Port, Routes(), kind);
        proxy.Server.ResponseCompression = On();

        using var response = await SendAsync(proxy, kind, "/", Accept("br, gzip"));

        Assert.AreEqual(0, response.Content.Headers.ContentEncoding.Count);
        CollectionAssert.AreEqual(small, await response.Content.ReadAsByteArrayAsync());
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task AlreadyEncodedAndNoTransform_AreLeftAlone(ClientKind kind)
    {
        await using var encodedOrigin = new HtmlOrigin(Html, contentEncoding: "gzip");
        await using var encoded = await StartAsync(encodedOrigin.Port, Routes(), kind);
        encoded.Server.ResponseCompression = On();
        using (var response = await SendAsync(encoded, kind, "/", Accept("br, gzip")))
        {
            CollectionAssert.AreEqual(new[] { "gzip" }, response.Content.Headers.ContentEncoding.ToArray());
            CollectionAssert.AreEqual(Html, await response.Content.ReadAsByteArrayAsync(),
                "an origin-encoded body is relayed byte for byte");
        }

        await using var noTransformOrigin = new HtmlOrigin(Html, cacheControl: "public, no-transform");
        await using var noTransform = await StartAsync(noTransformOrigin.Port, Routes(), kind);
        noTransform.Server.ResponseCompression = On();
        using (var response = await SendAsync(noTransform, kind, "/", Accept("br, gzip")))
        {
            Assert.AreEqual(0, response.Content.Headers.ContentEncoding.Count);
            CollectionAssert.AreEqual(Html, await response.Content.ReadAsByteArrayAsync());
        }
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task RepeatedRequests_AllRoundTrip(ClientKind kind)
    {
        await using var origin = new HtmlOrigin(Html);
        await using var proxy = await StartAsync(origin.Port, Routes(), kind);
        proxy.Server.ResponseCompression = On();

        for (var i = 0; i < 3; i++)
        {
            using var response = await SendAsync(proxy, kind, "/n" + i, Accept("br"));
            CollectionAssert.AreEqual(Html, Decode("br", await response.Content.ReadAsByteArrayAsync()));
        }

        Assert.AreEqual(3, origin.Hits);
    }

    /// <summary>HTTP/1.1 origin that answers every request with a fixed 200 and the headers it was given.</summary>
    private sealed class HtmlOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new();
        private readonly byte[] body;
        private readonly string extraHeaders;
        private readonly Task loop;
        private int hits;

        public HtmlOrigin(byte[] body, string? etag = null, string? vary = null, string? contentEncoding = null,
            string? cacheControl = null)
        {
            this.body = body;
            var headers = new StringBuilder();
            if (etag is not null) headers.Append("ETag: ").Append(etag).Append("\r\n");
            if (vary is not null) headers.Append("Vary: ").Append(vary).Append("\r\n");
            if (contentEncoding is not null) headers.Append("Content-Encoding: ").Append(contentEncoding).Append("\r\n");
            if (cacheControl is not null) headers.Append("Cache-Control: ").Append(cacheControl).Append("\r\n");
            extraHeaders = headers.ToString();
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = AcceptAsync();
        }

        public int Port { get; }

        public int Hits => Volatile.Read(ref hits);

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            listener.Stop();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The accept loop ends when the listener stops.
            }

            cts.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var read = 0;
                    var window = new List<byte>();
                    while (!cts.IsCancellationRequested)
                    {
                        // One request head per iteration; the proxy keeps the connection alive.
                        window.Clear();
                        while (true)
                        {
                            var n = await stream.ReadAsync(buffer.AsMemory(0, 1), cts.Token);
                            if (n == 0)
                            {
                                return;
                            }

                            window.Add(buffer[0]);
                            read = window.Count;
                            if (read >= 4 && window[read - 4] == '\r' && window[read - 3] == '\n'
                                && window[read - 2] == '\r' && window[read - 1] == '\n')
                            {
                                break;
                            }
                        }

                        Interlocked.Increment(ref hits);
                        var head = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " +
                            body.Length + "\r\n" + extraHeaders + "\r\n");
                        await stream.WriteAsync(head, cts.Token);
                        await stream.WriteAsync(body, cts.Token);
                        await stream.FlushAsync(cts.Token);
                    }
                }
            }
            catch
            {
                // The proxy closed the socket.
            }
        }
    }
}
