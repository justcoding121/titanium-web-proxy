using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Characterization for issue #911: large fixed-length downloads (~200MB) through the proxy.
/// </summary>
[TestClass]
[TestCategory("Slow")]
public class LargeDownloadTests
{
    private const int PayloadBytes = 200 * 1024 * 1024;

    [TestMethod]
    [Timeout(5 * 60 * 1000)]
    public async Task FixedLength_200MB_Download_Completes_WithBoundedWorkingSetGrowth()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        server.HandleRequest(async context =>
        {
            context.Response.ContentLength = PayloadBytes;
            context.Response.ContentType = "application/octet-stream";
            var buffer = new byte[64 * 1024];
            new Random(42).NextBytes(buffer);
            var remaining = PayloadBytes;
            while (remaining > 0)
            {
                var n = Math.Min(buffer.Length, remaining);
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, n));
                remaining -= n;
            }
        });

        var proxy = testSuite.GetProxy();
        using var client = testSuite.GetClient(proxy);
        client.Timeout = TimeSpan.FromMinutes(4);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = Process.GetCurrentProcess().WorkingSet64;

        using var response = await client.GetAsync(server.ListeningHttpUrl, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(PayloadBytes, response.Content.Headers.ContentLength);

        var total = 0L;
        await using (var stream = await response.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
                total += read;
        }

        Assert.AreEqual(PayloadBytes, total, "Full 200MB payload must be delivered.");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = Process.GetCurrentProcess().WorkingSet64;
        var growthMb = (after - before) / (1024.0 * 1024.0);

        // Streaming should not retain the whole body; allow generous headroom for GC/allocator noise.
        Assert.IsTrue(growthMb < 80,
            $"Working set grew by {growthMb:F1} MB; expected streaming without buffering the full 200MB.");
    }
}
