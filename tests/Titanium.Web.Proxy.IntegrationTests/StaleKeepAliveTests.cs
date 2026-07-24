using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Characterization for issue #772: after an idle origin closes a keep-alive connection,
///     the next bodyless request must succeed via retry on a fresh upstream connection.
/// </summary>
[TestClass]
public class StaleKeepAliveTests
{
    private static readonly Encoding MsgEncoding = HttpHelper.GetEncodingFromContentType(null);

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task IdleOriginClose_NextBodylessRequest_SucceedsViaRetry()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        var acceptCount = 0;
        var firstRequestDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.HandleTcpRequest(async context =>
        {
            var n = Interlocked.Increment(ref acceptCount);
            await DrainRequestHeaders(context);

            var response = MsgEncoding.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Length: 2\r\n" +
                "Connection: keep-alive\r\n" +
                "\r\n" +
                "ok");
            await context.Transport.Output.WriteAsync(response);

            if (n == 1)
            {
                firstRequestDone.TrySetResult(true);
                // Idle close from the origin while the proxy may still hold the connection in its pool.
                await Task.Delay(200);
                context.Transport.Output.Complete();
                context.Transport.Input.Complete();
            }
            else
            {
                // Keep the second connection open briefly so the client can finish reading.
                await Task.Delay(500);
                context.Transport.Output.Complete();
            }
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.EnableConnectionPool = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();
        var proxyUrl = new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}/");

        var first = await client.GetAsync(proxyUrl);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual("ok", await first.Content.ReadAsStringAsync());
        Assert.IsTrue(await firstRequestDone.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // Give the origin time to close the idle connection before the next request.
        await Task.Delay(400);

        var second = await client.GetAsync(proxyUrl);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual("ok", await second.Content.ReadAsStringAsync());

        Assert.IsTrue(acceptCount >= 2,
            $"Expected a fresh upstream accept after idle close; acceptCount={acceptCount}");
    }

    private static async Task DrainRequestHeaders(ConnectionContext context)
    {
        var requestText = string.Empty;
        while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var result = await context.Transport.Input.ReadAsync();
            foreach (var seg in result.Buffer) requestText += MsgEncoding.GetString(seg.Span);
            context.Transport.Input.AdvanceTo(result.Buffer.End);
        }
    }
}
