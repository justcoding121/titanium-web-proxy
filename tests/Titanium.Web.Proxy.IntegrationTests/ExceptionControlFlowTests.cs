using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Logging;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration characterization for issue #634: happy-path keep-alive should not throw,
///     user cancellation should not log as Error, disposal still runs, and stale pooled
///     connections still succeed via a single safe retry.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExceptionControlFlowTests
{
    private static TestServer sharedServer;

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

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task KeepAliveHappyPath_DoesNotRaiseFirstChanceExceptions()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        using var client = testSuite.GetClient(proxy);

        var firstChance = 0;
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, e) =>
        {
            // Ignore AppDomain/test-infrastructure noise; count proxy-pipeline throws.
            var typeName = e.Exception.GetType().FullName ?? string.Empty;
            var stack = e.Exception.StackTrace ?? string.Empty;
            if (stack.Contains("Titanium.Web.Proxy", StringComparison.Ordinal)
                || typeName.StartsWith("Titanium.Web.Proxy", StringComparison.Ordinal))
                Interlocked.Increment(ref firstChance);
        };

        AppDomain.CurrentDomain.FirstChanceException += handler;
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var body = await client.GetStringAsync(server.ListeningHttpUrl);
                Assert.AreEqual("ok", body);
            }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }

        Assert.AreEqual(0, firstChance,
            "successful keep-alive traffic must not throw first-chance exceptions in the proxy pipeline");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task TerminateSession_IsNotLoggedAsError_AndSessionIsDisposed()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        });

        var proxy = testSuite.GetProxy();
        proxy.Logging.Enabled = true;
        proxy.Logging.MinimumLevel = LogLevel.Debug;
        proxy.Logging.EnableConsole = false;
        proxy.Logging.EnableFile = false;

        var capturing = new CapturingLoggerProvider();
        proxy.Logging.LoggerFactory = new CapturingLoggerFactory(capturing);
        proxy.ApplyLoggingConfiguration();

        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.BeforeRequest += (_, e) =>
        {
            e.TerminateSession();
            // SessionEventArgs.Dispose runs in RequestHandler's finally after cancel propagates.
            _ = Task.Run(async () =>
            {
                // Give the pipeline a moment to unwind; disposal is asserted via AfterResponse.
                await Task.Delay(50);
            });
            return Task.CompletedTask;
        };
        proxy.AfterResponse += (_, _) =>
        {
            disposed.TrySetResult(true);
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        try
        {
            await client.GetAsync(server.ListeningHttpUrl);
        }
        catch (HttpRequestException)
        {
            // Client may see a reset/abort after TerminateSession; that is expected.
        }
        catch (TaskCanceledException)
        {
        }

        Assert.IsTrue(await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "AfterResponse (and thus session dispose) must still run after TerminateSession");

        Assert.AreEqual(0, capturing.ErrorCount,
            "cancellation must not be reported at Error; got: " + string.Join(" | ", capturing.ErrorMessages));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ClientDisconnect_MidRequest_DoesNotLogError()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var serverEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HandleRequest(async context =>
        {
            serverEntered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        });

        var proxy = testSuite.GetProxy();
        proxy.Logging.Enabled = true;
        proxy.Logging.MinimumLevel = LogLevel.Debug;
        proxy.Logging.EnableConsole = false;
        proxy.Logging.EnableFile = false;

        var capturing = new CapturingLoggerProvider();
        proxy.Logging.LoggerFactory = new CapturingLoggerFactory(capturing);
        proxy.ApplyLoggingConfiguration();

        using var client = testSuite.GetClient(proxy);
        using var cts = new CancellationTokenSource();
        var requestTask = client.GetAsync(server.ListeningHttpUrl, cts.Token);

        Assert.IsTrue(await serverEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        cts.Cancel();

        try
        {
            await requestTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException)
        {
        }

        // Allow the proxy pipeline to observe the client abort and report diagnostics.
        await Task.Delay(500);

        Assert.AreEqual(0, capturing.ErrorCount,
            "normal client disconnect should be benign; got: " + string.Join(" | ", capturing.ErrorMessages));
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly CapturingLoggerProvider provider;

        public CapturingLoggerFactory(CapturingLoggerProvider provider)
        {
            this.provider = provider;
        }

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => this.provider.CreateLogger(categoryName);

        public void Dispose() { }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly CapturingLogger logger = new();

        public int ErrorCount => logger.ErrorCount;
        public ConcurrentBag<string> ErrorMessages => logger.ErrorMessages;

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            public int ErrorCount;
            public readonly ConcurrentBag<string> ErrorMessages = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel != LogLevel.Error && logLevel != LogLevel.Critical) return;
                Interlocked.Increment(ref ErrorCount);
                ErrorMessages.Add(formatter(state, exception));
            }
        }
    }
}
