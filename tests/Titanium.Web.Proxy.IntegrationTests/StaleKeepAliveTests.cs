using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Logging;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Characterization for issue #772: after an idle origin closes a keep-alive connection,
///     the next bodyless request must succeed via retry on a fresh upstream connection.
///     Also serves exception-performance Ship 4 load gate: under forced origin idle-close the
///     RetryPolicy path fires O(1) per stale reuse — not hot enough to justify result-shaping.
/// </summary>
[TestClass]
[DoNotParallelize] // Uses global AppDomain.FirstChanceException; isolate to avoid cross-test interference
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
        proxy.Logging.Enabled = true;
        proxy.Logging.MinimumLevel = LogLevel.Debug;
        proxy.Logging.EnableConsole = false;
        proxy.Logging.EnableFile = false;
        var capturing = new RetryGateLoggerProvider();
        proxy.Logging.LoggerFactory = new RetryGateLoggerFactory(capturing);
        proxy.ApplyLoggingConfiguration();

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

        // Ship 4 gate: under forced origin idle-close, RetryPolicy must not look browse-hot.
        // A successful second accept is the behavioral proof; the Debug breadcrumb may be 0 when
        // the pool discards the dead socket without throwing RetryableServerConnectionException,
        // or 1–2 when the typed retry path runs — either is fine for "do not result-shape yet".
        Assert.IsTrue(capturing.RetryPolicyCaughtCount <= 2,
            $"RetryPolicy path unexpectedly hot under single stale reuse; got {capturing.RetryPolicyCaughtCount}. Messages: "
            + string.Join(" | ", capturing.DebugMessages));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task MalformedStatusOnReusedConnection_BodylessRequest_SucceedsViaRetry()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        var acceptCount = 0;
        var requestsOnFirstConnection = 0;

        server.HandleTcpRequest(async context =>
        {
            var n = Interlocked.Increment(ref acceptCount);
            await DrainRequestHeaders(context);

            if (n == 1)
            {
                var response = MsgEncoding.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Length: 2\r\n" +
                    "Connection: keep-alive\r\n" +
                    "\r\n" +
                    "ok");
                await context.Transport.Output.WriteAsync(response);

                // Same socket, next request: bytes that are not an HTTP status line.
                await DrainRequestHeaders(context);
                Interlocked.Increment(ref requestsOnFirstConnection);
                await context.Transport.Output.WriteAsync(MsgEncoding.GetBytes("NOTHTTP\r\n"));
                await Task.Delay(500);
                return;
            }

            var retry = MsgEncoding.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Length: 2\r\n" +
                "Connection: close\r\n" +
                "\r\n" +
                "ok");
            await context.Transport.Output.WriteAsync(retry);
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

        var second = await client.GetAsync(proxyUrl);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual("ok", await second.Content.ReadAsStringAsync());

        Assert.IsTrue(requestsOnFirstConnection >= 1,
            "The pooled connection was not reused, so the malformed status line was never read.");
        Assert.IsTrue(acceptCount >= 2,
            $"Expected a fresh upstream accept after the malformed status line; acceptCount={acceptCount}");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task MalformedStatusOnReusedConnection_PostRequest_IsNotReplayed()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        var acceptCount = 0;
        var postsSeen = 0;

        server.HandleTcpRequest(async context =>
        {
            var n = Interlocked.Increment(ref acceptCount);
            await DrainRequestHeaders(context);

            if (n == 1)
            {
                await context.Transport.Output.WriteAsync(MsgEncoding.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok"));

                // Same socket, the POST: answer with bytes that are not an HTTP status line.
                await DrainRequestHeaders(context);
                Interlocked.Increment(ref postsSeen);
                await context.Transport.Output.WriteAsync(MsgEncoding.GetBytes("NOTHTTP\r\n"));
                await Task.Delay(500);
                return;
            }

            // A replay of the POST would land on a fresh connection.
            Interlocked.Increment(ref postsSeen);
            await context.Transport.Output.WriteAsync(MsgEncoding.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
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

        try
        {
            using var post = await client.PostAsync(proxyUrl, new StringContent("x"));
            Assert.AreNotEqual(HttpStatusCode.OK, post.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Connection torn down by the proxy is also an acceptable non-replay outcome.
        }

        await Task.Delay(300);
        Assert.AreEqual(1, acceptCount, "A POST must not be replayed on a fresh upstream connection.");
        Assert.AreEqual(1, postsSeen, "The POST must reach the origin exactly once.");
    }

    /// <summary>
    ///     A page load leaves several idle upstream sockets pooled. A dev server idle-closes all of them
    ///     together, and the pool cannot see that (its liveness poll only proves the local side is writable).
    ///     The replay after the first stale socket must dial; renting the next queued socket fails the same
    ///     way and surfaced as "502: the origin closed the connection before sending a response".
    /// </summary>
    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false, DisplayName = "fast-path")]
    [DataRow(true, DisplayName = "session-hooks")]
    public async Task SeveralPooledSocketsIdleClosed_NextRequest_ReplaysOnFreshDial(bool withHooks)
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        const int pooled = 3;
        var accepted = 0;
        var allPooledAccepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.HandleTcpRequest(async context =>
        {
            var n = Interlocked.Increment(ref accepted);
            if (n == pooled) allPooledAccepted.TrySetResult(true);

            await DrainRequestHeaders(context);
            if (n <= pooled)
            {
                // Hold every response until all sockets exist, so the proxy holds distinct ones.
                await allPooledAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await context.Transport.Output.WriteAsync(MsgEncoding.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok"));

            if (n <= pooled)
            {
                // Idle close while the proxy still pools the socket.
                await Task.Delay(200);
                context.Transport.Output.Complete();
                context.Transport.Input.Complete();
            }
            else
            {
                await Task.Delay(500);
            }
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableConnectionPool = true;
        if (withHooks)
        {
            // A registered handler takes requests off the fast path and onto RetryPolicy (the Inspector's path).
            proxy.BeforeRequest += (_, _) => Task.CompletedTask;
        }

        var warmups = Enumerable.Range(0, pooled)
            .Select(_ => testSuite.GetClient(proxy).GetAsync(server.ListeningTcpUrl))
            .ToArray();
        foreach (var warmup in await Task.WhenAll(warmups))
        {
            Assert.AreEqual(HttpStatusCode.OK, warmup.StatusCode);
        }

        // Let the origin close every idle socket, and the proxy release the sockets to its pool.
        await Task.Delay(700);

        var next = await testSuite.GetClient(proxy).GetAsync(server.ListeningTcpUrl);
        Assert.AreEqual(HttpStatusCode.OK, next.StatusCode,
            "replay rented another stale pooled socket instead of dialing a fresh connection");
        Assert.AreEqual("ok", await next.Content.ReadAsStringAsync());
        Assert.IsTrue(Volatile.Read(ref accepted) > pooled,
            $"expected a fresh upstream accept for the replay; accepted={accepted}");
    }

    /// <summary>
    ///     Guards the replay fix against over-reach: only a replay may skip the pool. A first attempt
    ///     on a healthy pooled socket must still reuse it, or every request pays a new TCP/TLS handshake.
    /// </summary>
    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false, DisplayName = "fast-path")]
    [DataRow(true, DisplayName = "session-hooks")]
    public async Task HealthyPooledSocket_FirstAttempt_StillReusesPool(bool withHooks)
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        var accepted = 0;
        server.HandleTcpRequest(async context =>
        {
            Interlocked.Increment(ref accepted);
            while (true)
            {
                await DrainRequestHeaders(context);
                await context.Transport.Output.WriteAsync(MsgEncoding.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok"));
            }
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableConnectionPool = true;
        if (withHooks)
        {
            // A registered handler takes requests off the fast path and onto RetryPolicy (the Inspector's path).
            proxy.BeforeRequest += (_, _) => Task.CompletedTask;
        }

        const int requests = 5;
        for (var i = 0; i < requests; i++)
        {
            // A new client connection each time, so only the proxy's upstream pool can supply the socket.
            var response = await testSuite.GetClient(proxy).GetAsync(server.ListeningTcpUrl);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync();
            await Task.Delay(150);
        }

        Assert.AreEqual(1, Volatile.Read(ref accepted),
            "healthy pooled upstream socket was not reused; the first attempt must use the pool");
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

    private sealed class RetryGateLoggerFactory : ILoggerFactory
    {
        private readonly RetryGateLoggerProvider provider;

        public RetryGateLoggerFactory(RetryGateLoggerProvider provider) => this.provider = provider;

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => this.provider.CreateLogger(categoryName);

        public void Dispose() { }
    }

    private sealed class RetryGateLoggerProvider : ILoggerProvider
    {
        private readonly RetryGateLogger logger = new();

        public int RetryPolicyCaughtCount => logger.RetryPolicyCaughtCount;
        public ConcurrentBag<string> DebugMessages => logger.DebugMessages;

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose() { }

        private sealed class RetryGateLogger : ILogger
        {
            public int RetryPolicyCaughtCount;
            public readonly ConcurrentBag<string> DebugMessages = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel > LogLevel.Debug) return;
                var message = formatter(state, exception);
                DebugMessages.Add(message);
                if (message.Contains("RetryPolicy caught candidate for retry", StringComparison.Ordinal))
                    Interlocked.Increment(ref RetryPolicyCaughtCount);
            }
        }
    }
}
