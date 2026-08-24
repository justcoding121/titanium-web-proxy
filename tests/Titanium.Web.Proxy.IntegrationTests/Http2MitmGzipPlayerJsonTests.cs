using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression for YouTube-style failures under Basic DEBUG MITM: observational
///     <c>BeforeRequest</c>/<c>BeforeResponse</c>/<c>AfterResponse</c> hooks force H2 header
///     decode/re-encode. If <c>content-encoding</c> is lost or the gzip body is double-compressed,
///     the browser parses compressed player JSON as text and builds garbage
///     <c>videoplayback?expire=…</c> URLs that googlevideo rejects with 403.
/// </summary>
[TestClass]
public class Http2MitmGzipPlayerJsonTests
{
    // Stable progressive URL shape (sane expire / mn / sig) embedded like youtubei player JSON.
    private const string SaneStreamingUrl =
        "https://rr1---sn-q4fzen7e.googlevideo.com/videoplayback?expire=1787611688" +
        "&ei=yHWMar_vLLzHlu8PyqfuqQI&ip=47.162.20.80&id=o-AHGLyC4PgsXhZwqss0bUOAE9o8VGeCTq5pZ96n2farJA" +
        "&itag=18&source=youtube&requiressl=yes&mh=2x&mm=31%2C29&mn=sn-q4fzen7e%2Csn-q4fl6ndz" +
        "&ms=au%2Crdu&mv=m&mvi=1&pl=17&sig=AOq0QJ8wRAIgSaneSignatureExample1234567890abcdef" +
        "&lsig=DifferentLsigExample9876543210fedcba";

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Http2_Mitm_ObservationalHooks_GzipPlayerJson_PreservesStreamingUrls_Concurrent()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        var plainJson = Encoding.UTF8.GetBytes(
            "{\"streamingData\":{\"formats\":[{\"itag\":18,\"url\":\"" + SaneStreamingUrl + "\"}]}}");
        var gzipped = GzipCompress(plainJson);

        server.HandleRequest(async context =>
        {
            context.Response.Headers.ContentEncoding = "gzip";
            context.Response.Headers.ContentType = "application/json";
            context.Response.ContentLength = gzipped.Length;
            // Piecewise write so H2 DATA frames stream (matches CDN player responses).
            const int piece = 512;
            for (var offset = 0; offset < gzipped.Length; offset += piece)
            {
                var len = Math.Min(piece, gzipped.Length - offset);
                await context.Response.Body.WriteAsync(gzipped.AsMemory(offset, len));
                await context.Response.Body.FlushAsync();
            }
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        // Basic example traffic-tape shape: hooks subscribed, bodies never read.
        proxy.BeforeRequest += (_, _) => Task.CompletedTask;
        proxy.BeforeResponse += (_, _) => Task.CompletedTask;
        proxy.AfterResponse += (_, _) => Task.CompletedTask;

        using var client = TestHelper.GetHttp2Client(proxy);
        // No AutomaticDecompression — assert Content-Encoding + wire gzip integrity.
        var url = server.ListeningHttpsUrl.TrimEnd('/') + "/youtubei/v1/player";

        var tasks = Enumerable.Range(0, 16).Select(async i =>
        {
            using var response = await client.GetAsync(url + "?i=" + i);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "stream " + i);
            Assert.AreEqual(new Version(2, 0), response.Version, "stream " + i);
            Assert.IsTrue(response.Content.Headers.ContentEncoding.Contains("gzip"),
                "content-encoding must survive MITM HPACK re-encode (stream " + i + ")");

            var wire = await response.Content.ReadAsByteArrayAsync();
            var plain = GzipDecompress(wire);
            var text = Encoding.UTF8.GetString(plain);

            Assert.IsTrue(text.Contains("expire=1787611688", StringComparison.Ordinal),
                "Player JSON must keep the sane expire= value (stream " + i + "). " +
                "Garbage expires (e.g. 4113260117) indicate compressed bytes parsed as text.");
            Assert.IsTrue(text.Contains("mn=sn-q4fzen7e", StringComparison.Ordinal),
                "Player JSON must keep sane mn= host tokens (stream " + i + ").");
            Assert.IsFalse(text.Contains("expire=4113260117", StringComparison.Ordinal));
            CollectionAssert.AreEqual(plainJson, plain, "stream " + i);
        });

        await Task.WhenAll(tasks);
    }

    private static byte[] GzipCompress(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(plain, 0, plain.Length);
        return ms.ToArray();
    }

    private static byte[] GzipDecompress(byte[] gzipped)
    {
        using var input = new MemoryStream(gzipped);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        gzip.CopyTo(plain);
        return plain.ToArray();
    }
}
