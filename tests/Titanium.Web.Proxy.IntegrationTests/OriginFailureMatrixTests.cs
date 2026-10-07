using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression-2026-10-07 matrix (ledger item 4): every way the origin can fail before a response byte is
///     committed on a decrypted HTTP/1.1 tunnel must reach the client as a real 502 (or 504 for deadlines) with
///     a generic body and <c>Connection: close</c>, never as a silent close. Sessions that already committed
///     bytes, were answered by the user, or were cancelled by the client must not get a second response.
/// </summary>
[TestClass]
[DoNotParallelize]
public class OriginFailureMatrixTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    [DataRow(OriginAction.ResetBeforeTls, "GET", 0, DisplayName = "tls-rst-get")]
    [DataRow(OriginAction.CloseWithoutResponse, "POST", 1, DisplayName = "eof-before-response-post-no-replay")]
    [DataRow(OriginAction.CloseWithoutResponse, "GET", -1, DisplayName = "eof-before-response-get-retried-then-502")]
    public async Task OriginFailureBeforeResponse_ClientGets502(
        OriginAction failure, string method, int maxRequests)
    {
        // Every connection fails the same way, so a retry (if any) fails too: the double
        // "RetryPolicy caught candidate" -> "Unhandled exception" sequence from the session log.
        using var origin = new ScriptedTlsOrigin((_, request) =>
            request == -1 && failure != OriginAction.ResetBeforeTls ? OriginAction.Respond : failure);

        var logs = new LevelCapturingLoggerFactory();
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.Logging.LoggerFactory = logs;
        proxy.ApplyLoggingConfiguration();

        var afterResponse = new TaskCompletionSource<(int Status, Exception? Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var afterResponseCalls = 0;
        proxy.AfterResponse += (_, e) =>
        {
            Interlocked.Increment(ref afterResponseCalls);
            afterResponse.TrySetResult((e.HttpClient.Response.StatusCode, e.Exception));
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);
        client.Timeout = TimeSpan.FromSeconds(30);

        using var request = new HttpRequestMessage(new HttpMethod(method), $"https://localhost:{origin.Port}/");
        if (method == "POST")
            request.Content = new StringContent("abc");
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.IsTrue(response.Headers.ConnectionClose == true, "a failed exchange must close the client connection");
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(string.IsNullOrWhiteSpace(body));
        StringAssert.DoesNotMatch(body, new System.Text.RegularExpressions.Regex("Exception|at Titanium|Socket"),
            "the 502 body must stay generic (no stack or resolver text)");

        // -1: an idempotent GET is bounded: the first attempt, a silent fast-path re-dial (covers a pool of
        // connections that all died together), then NetworkFailureRetryAttempts retries - never unbounded.
        if (maxRequests < 0) maxRequests = proxy.NetworkFailureRetryAttempts + 2;
        Assert.IsTrue(origin.Requests <= maxRequests && origin.Accepted <= maxRequests + 3,
            $"origin saw {origin.Requests} requests on {origin.Accepted} connections, expected <= {maxRequests} requests (no retry storm)");

        var observed = await afterResponse.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(502, observed.Status);
        Assert.IsNotNull(observed.Error, "AfterResponse must see the failure in args.Exception");
        await Task.Delay(200);
        Assert.AreEqual(1, Volatile.Read(ref afterResponseCalls), "AfterResponse must fire exactly once");
        Assert.AreEqual(0, logs.Count(LogLevel.Error), logs.Describe(LogLevel.Warning));
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task OriginTimeout_ClientGets504()
    {
        using var origin = new ScriptedTlsOrigin((_, request) =>
            request == -1 ? OriginAction.Respond : OriginAction.NeverRespond);

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.ResponseHeaderTimeoutSeconds = 1;

        var client = testSuite.GetClient(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);

        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync($"https://localhost:{origin.Port}/");
        sw.Stop();

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10), $"timeout should fire near 1 s, took {sw.Elapsed}");
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    [DataRow(false, DisplayName = "fast-path")]
    [DataRow(true, DisplayName = "session-hooks")]
    public async Task ResponseAlreadyCommitted_NoSecondResponse(bool withHooks)
    {
        // Headers + partial body were already sent, then the origin drops: the client must see the 200 it was
        // given (and a truncated body), never a 502 spliced into the middle of the stream.
        using var origin = new ScriptedTlsOrigin((_, request) =>
            request == -1 ? OriginAction.Respond : OriginAction.PartialBodyThenClose);

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        if (withHooks)
        {
            proxy.BeforeResponse += (_, _) => Task.CompletedTask;
            proxy.AfterResponse += (_, _) => Task.CompletedTask;
        }

        var client = testSuite.GetClient(proxy);
        client.Timeout = TimeSpan.FromSeconds(20);

        using var response = await client.GetAsync($"https://localhost:{origin.Port}/",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        try { var bytes = await response.Content.ReadAsByteArrayAsync(); Assert.Fail($"no exception; len={bytes.Length}; headers={response.Headers}{response.Content.Headers}"); } catch (HttpRequestException) { }
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task UserSuppliedResponse_NotOverwritten()
    {
        var refusedPort = GetRefusedPort();

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.GenericResponse("handled by user", HttpStatusCode.Gone);
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);
        using var response = await client.GetAsync($"https://127.0.0.1:{refusedPort}/");

        Assert.AreEqual(HttpStatusCode.Gone, response.StatusCode);
        Assert.AreEqual("handled by user", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task ClientCancelled_NoErrorLogged_AndProxyStaysHealthy()
    {
        using var origin = new ScriptedTlsOrigin((_, request) =>
            request == -1 ? OriginAction.Respond : (request == 0 ? OriginAction.NeverRespond : OriginAction.Respond));

        var logs = new LevelCapturingLoggerFactory();
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.Logging.LoggerFactory = logs;
        proxy.ApplyLoggingConfiguration();

        var client = testSuite.GetClient(proxy);
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await client.GetAsync($"https://localhost:{origin.Port}/", cts.Token));
        }

        await Task.Delay(300);
        Assert.AreEqual(0, logs.Count(LogLevel.Error), logs.Describe(LogLevel.Warning));

        // A new request through the same proxy still works.
        using var healthy = new ScriptedTlsOrigin((_, _) => OriginAction.Respond);
        using var ok = await client.GetAsync($"https://localhost:{healthy.Port}/");
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task KeepAliveSessionContinuesAfterSuccessfulRequest_ThenFailure_ThenSuccess()
    {
        using var good = new ScriptedTlsOrigin((_, _) => OriginAction.Respond);
        var refusedPort = GetRefusedPort();

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        var client = testSuite.GetClient(proxy);

        using (var first = await client.GetAsync($"https://localhost:{good.Port}/"))
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);

        using (var failed = await client.GetAsync($"https://127.0.0.1:{refusedPort}/"))
            Assert.AreEqual(HttpStatusCode.BadGateway, failed.StatusCode);

        using (var again = await client.GetAsync($"https://localhost:{good.Port}/"))
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
    }

    private static int GetRefusedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
