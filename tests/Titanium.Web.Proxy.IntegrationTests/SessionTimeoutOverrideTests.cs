using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Issue #945: per-session timeout overrides on <see cref="EventArguments.SessionEventArgs" />.
/// </summary>
[TestClass]
public class SessionTimeoutOverrideTests
{
    private static readonly System.Text.Encoding MsgEncoding = HttpHelper.GetEncodingFromContentType(null);

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Per_Session_ResponseHeaderTimeout_Override_Shorter_Than_Server_Default_Wins()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);
            await Task.Delay(Timeout.Infinite, context.ConnectionClosed);
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.ResponseHeaderTimeoutSeconds = 30;

        proxy.BeforeRequest += (_, e) =>
        {
            e.ResponseHeaderTimeout = TimeSpan.FromSeconds(1);
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}/"));
        sw.Stop();

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Per-session 1s override should win over 30s server default; took {sw.Elapsed}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Per_Session_RequestTimeout_Returns_504_When_Exchange_Exceeds_Deadline()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);
            await Task.Delay(Timeout.Infinite, context.ConnectionClosed);
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.ResponseHeaderTimeoutSeconds = 0;

        ProxyTimeoutException observedTimeout = null;
        proxy.AfterResponse += (_, args) =>
        {
            observedTimeout = FindTimeout(args.Exception);
            return Task.CompletedTask;
        };

        proxy.BeforeRequest += (_, e) =>
        {
            e.RequestTimeout = TimeSpan.FromSeconds(1);
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        var response = await client.GetAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}/"));

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);

        for (var i = 0; i < 50 && observedTimeout == null; i++) await Task.Delay(20);
        Assert.IsNotNull(observedTimeout);
        Assert.AreEqual(ProxyTimeoutKind.Request, observedTimeout.Kind);
    }

    private static ProxyTimeoutException FindTimeout(Exception exception)
    {
        while (exception != null)
        {
            if (exception is ProxyTimeoutException timeout) return timeout;
            exception = exception.InnerException;
        }

        return null;
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
