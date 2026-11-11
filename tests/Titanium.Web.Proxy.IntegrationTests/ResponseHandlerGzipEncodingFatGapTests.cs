using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>ResponseHandler</c> suite: gzip-encoded origin bodies round-trip through explicit MITM.
/// </summary>
[TestClass]
public class ResponseHandlerGzipEncodingFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: gzip Content-Encoding responses are corrupted or truncated before the client.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_GzipEncodedBody_RoundTrips()
    {
        const string plain = "gzip-gap-payload-xyz";
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            ctx.Response.Headers.ContentEncoding = "gzip";
            ctx.Response.ContentType = "text/plain";
            using var ms = new MemoryStream();
            await using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                await gz.WriteAsync(System.Text.Encoding.UTF8.GetBytes(plain));
            await ctx.Response.Body.WriteAsync(ms.ToArray());
        });

        var proxy = suite.GetProxy();
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ServerCertificateCustomValidationCallback =
                (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/gz");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(plain, body);
    }

}
