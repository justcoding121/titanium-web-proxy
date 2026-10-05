using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Streamed bodies must not be aborted by the whole-body buffering budget, and a slow stream
///     must not stall its siblings on a pooled HTTP/2 origin connection.
/// </summary>
[DoNotParallelize]
[TestClass]
public class BridgeBodyRelayTests
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

    [TestMethod]
    [Timeout(60_000)]
    public async Task H1ToH2_KnownLengthAboveBodyBudget_IsRelayed()
    {
        var body = new byte[16 * 1024 * 1024];
        body.AsSpan().Fill(0x5A);
        var expected = SHA256.HashData(body);

        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body);
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.MaxBufferedBodyBytes = 1024;
        ForceHttp2Origin(proxy);

        var client = testSuite.GetClient(proxy);
        var received = await client.GetByteArrayAsync(new Uri(server.ListeningHttpsUrl));
        CollectionAssert.AreEqual(expected, SHA256.HashData(received));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H1ToH2_UnknownLengthAboveBodyBudget_IsRelayed()
    {
        const int length = 16 * 1024 * 1024;
        var chunk = new byte[64 * 1024];
        chunk.AsSpan().Fill(0x3C);

        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            for (var sent = 0; sent < length; sent += chunk.Length)
                await context.Response.Body.WriteAsync(chunk);
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.MaxBufferedBodyBytes = 1024;
        ForceHttp2Origin(proxy);

        var client = testSuite.GetClient(proxy);
        var received = await client.GetByteArrayAsync(new Uri(server.ListeningHttpsUrl));
        Assert.AreEqual(length, received.Length);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H1ToH1_KnownLengthAboveBodyBudget_IsRelayed()
    {
        var body = new byte[1024 * 1024];
        body.AsSpan().Fill(0x11);

        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body);
        });

        var proxy = testSuite.GetProxy();
        proxy.MaxBufferedBodyBytes = 1024;

        var client = testSuite.GetClient(proxy);
        var received = await client.GetByteArrayAsync(new Uri(server.ListeningHttpsUrl));
        Assert.AreEqual(body.Length, received.Length);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_ServerSentEvent_ArrivesBeforeTheOriginFinishes()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: hello\n\n");
            await context.Response.Body.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        ForceHttp2Origin(proxy);

        var client = testSuite.GetClient(proxy);
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl),
            HttpCompletionOption.ResponseHeadersRead);
        var started = Environment.TickCount64;
        using var reader = new System.IO.StreamReader(await response.Content.ReadAsStreamAsync());
        var line = await reader.ReadLineAsync();
        var elapsed = Environment.TickCount64 - started;
        Assert.AreEqual("data: hello", line);
        Assert.IsTrue(elapsed < 1500, $"first event took {elapsed} ms; the bridge buffered until the origin ended");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_SlowStream_DoesNotBlockASiblingOnTheSameOrigin()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HandleRequest(async context =>
        {
            if (context.Request.Path.Value?.Contains("slow", StringComparison.Ordinal) == true)
            {
                slowEntered.TrySetResult();
                var buf = new byte[64 * 1024];
                for (var i = 0; i < 32; i++)
                    await context.Response.Body.WriteAsync(buf);
                await Task.Delay(TimeSpan.FromSeconds(8), context.RequestAborted);
                return;
            }

            await context.Response.WriteAsync("fast");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        ForceHttp2Origin(proxy);
        var client = testSuite.GetClient(proxy);

        var slow = client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/slow"),
            HttpCompletionOption.ResponseHeadersRead);
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var fastCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var fast = await client.GetStringAsync(server.ListeningHttpsUrl + "/fast", fastCts.Token);
        Assert.AreEqual("fast", fast);
        _ = slow;
    }

    private static void ForceHttp2Origin(ProxyServer proxy)
    {
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
    }
}
