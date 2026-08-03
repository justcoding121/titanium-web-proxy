using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration coverage for Issues #852 / #945: response-header deadlines, typed
///     <see cref="ProxyTimeoutException" />, 504 before commit, and per-session overrides.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ProxyTimeoutTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    private static readonly Encoding MsgEncoding = HttpHelper.GetEncodingFromContentType(null);

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Stalled_Origin_Response_Headers_Times_Out_With_Typed_Reason_And_504()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);
            // Never send response status/headers.
            await Task.Delay(Timeout.Infinite, context.ConnectionClosed);
        });

        var capture = new TestExceptionCapture();
        var proxy = testSuite.GetReverseProxy();
        proxy.Logging.LoggerFactory = capture;
        proxy.ApplyLoggingConfiguration();
        proxy.ResponseHeaderTimeoutSeconds = 1;

        ProxyTimeoutException? observedTimeout = null;
        proxy.AfterResponse += (_, args) =>
        {
            observedTimeout = FindTimeout(args.Exception);
            return Task.CompletedTask;
        };

        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}/"));
        sw.Stop();
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.IsTrue(body.Contains("Gateway Timeout", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected timeout near 1s, took {sw.Elapsed}");

        for (var i = 0; i < 50 && observedTimeout == null; i++) await Task.Delay(20);

        var timeout = observedTimeout ?? FindTimeout(capture.LastException);
        Assert.IsNotNull(timeout, "ProxyTimeoutException should be surfaced via session/diagnostics");
        Assert.AreEqual(ProxyTimeoutKind.ResponseHeader, timeout.Kind);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Active_Transfer_Is_Not_Killed_By_Short_Header_Deadline_After_Headers_Arrive()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var headers = MsgEncoding.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Length: 10\r\n" +
                "Connection: close\r\n" +
                "\r\n");
            await context.Transport.Output.WriteAsync(headers);
            await context.Transport.Output.FlushAsync();

            // Body arrives slowly over > header deadline; transfer must still complete.
            await Task.Delay(1500);
            await context.Transport.Output.WriteAsync(MsgEncoding.GetBytes("0123456789"));
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.ResponseHeaderTimeoutSeconds = 1;

        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        var response = await client.GetAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}/"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("0123456789", body);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Server_RequestTimeoutSeconds_Returns_504_When_Exchange_Exceeds_Deadline()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);
            await Task.Delay(Timeout.Infinite, context.ConnectionClosed);
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.ResponseHeaderTimeoutSeconds = 0;
        proxy.RequestTimeoutSeconds = 1;

        ProxyTimeoutException? observedTimeout = null;
        proxy.AfterResponse += (_, args) =>
        {
            observedTimeout = FindTimeout(args.Exception);
            return Task.CompletedTask;
        };

        proxy.BeforeRequest += (_, e) =>
        {
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

    private static ProxyTimeoutException? FindTimeout(Exception? exception)
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
