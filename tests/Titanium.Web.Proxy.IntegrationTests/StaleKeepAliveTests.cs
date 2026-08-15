using System;
using System.Collections.Concurrent;
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
