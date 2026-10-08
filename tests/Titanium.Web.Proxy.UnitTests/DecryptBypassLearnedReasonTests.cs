using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Regression-2026-10-07 (ledger item 11): the learned-bypass WARN ("EnableDecryptFailureBypass learned ...")
///     fired four times in the session with no hint why. The text must name the reason so an operator can tell
///     an origin TLS failure from an HTTP block or a client that rejected the proxy certificate.
/// </summary>
[TestClass]
public class DecryptBypassLearnedReasonTests
{
    private static (ProxyServer Proxy, CapturingFactory Logs) NewProxy()
    {
        var logs = new CapturingFactory();
        var proxy = new ProxyServer { EnableDecryptFailureBypass = true, ClientHandshakeRejectThreshold = 1 };
        proxy.Logging.LoggerFactory = logs;
        proxy.ApplyLoggingConfiguration();
        return (proxy, logs);
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void OriginTlsFailure_WarnNamesReason()
    {
        var (proxy, logs) = NewProxy();
        using (proxy)
        {
            // Forced learn is the path taken after a failed MITM handshake; the reason must still be named.
            proxy.ForceDecryptFailureBypass("tls.example.com");
            var warn = logs.Warnings.Single(m => m.Contains("tls.example.com"));
            StringAssert.Contains(warn, "learned");
            StringAssert.Contains(warn, "forced after a failed MITM handshake");
        }
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void HttpBlock_WarnNamesStatus()
    {
        var (proxy, logs) = NewProxy();
        using (proxy)
        {
            Assert.IsTrue(proxy.TryRecordDecryptFailureFromHttpStatus("waf.example.com", 403, forceImmediate: true));
            var warn = logs.Warnings.Single(m => m.Contains("waf.example.com"));
            StringAssert.Contains(warn, "HTTP 403 block");
        }
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void ClientRejectedCertificate_WarnNamesReason()
    {
        var (proxy, logs) = NewProxy();
        using (proxy)
        {
            Assert.IsTrue(proxy.TryRecordClientHandshakeReject("pinned.example.com", new IOException("eof")));
            var warn = logs.Warnings.Single(m => m.Contains("pinned.example.com"));
            StringAssert.Contains(warn, "rejected the proxy certificate");
        }
    }

    private sealed class CapturingFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> entries = new();

        public string[] Warnings => entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Logger(entries);

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly ConcurrentQueue<(LogLevel Level, string Message)> sink;

            public Logger(ConcurrentQueue<(LogLevel Level, string Message)> sink) => this.sink = sink;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
