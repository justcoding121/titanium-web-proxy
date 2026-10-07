#pragma warning disable CA1416 // QuicException is only constructed to feed the classifier, never connected.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Logging;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Regression-2026-10-07 log-signature guards (ledger items 2, 7, 9, 10): repeated client handshake aborts
///     must not flood the log with stacks, and the benign shutdown signatures from the session log must
///     never rise to Error.
/// </summary>
[TestClass]
public class Regression20261007LoggingTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void HandshakeAbortStorm_LogsBoundedLines_OneLineNoStack()
    {
        // "Couldn't authenticate host 'api2.cursor.sh' ... Received an unexpected EOF" x12,851.
        var log = new CapturingLogger();
        var eof = new IOException("Received an unexpected EOF or 0 bytes from the transport stream.");

        for (var i = 0; i < 5000; i++)
            ProxyLog.ClientHandshakeAborted(log, "api2.cursor.sh", eof);

        Assert.IsTrue(log.Entries.Count <= 2, $"expected a bounded number of lines, got {log.Entries.Count}");
        Assert.AreEqual(1, log.Entries.Count, "all 5000 aborts happen inside one throttle window");
        Assert.IsNull(log.Entries[0].Exception, "no stack trace at Debug");
        StringAssert.Contains(log.Entries[0].Message, "api2.cursor.sh");
        Assert.AreEqual(LogLevel.Debug, log.Entries[0].Level);
    }

    [TestMethod]
    public void HandshakeAbort_NotLoggedWhenDebugDisabled()
    {
        var log = new CapturingLogger { Minimum = LogLevel.Error };
        ProxyLog.ClientHandshakeAborted(log, "example.com", new IOException("eof"));
        Assert.AreEqual(0, log.Entries.Count);
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void QuicShutdownExceptions_LoggedAtMostTrace()
    {
        // "HTTP/3 origin inbound uni-stream drain ended" x162 / "AcceptInboundStreamAsync ended" x37.
        foreach (var error in new[]
                 {
                     QuicError.ConnectionAborted, QuicError.ConnectionIdle, QuicError.ConnectionTimeout,
                     QuicError.OperationAborted
                 })
        {
            Assert.AreEqual(LogLevel.Trace,
                Http3OriginClientSession.ClassifyShutdownLevel(new QuicException(error, null, error.ToString())),
                error.ToString());
        }

        Assert.AreEqual(LogLevel.Trace, Http3OriginClientSession.ClassifyShutdownLevel(
            new QuicException(QuicError.InternalError, null, "Connection shutdown: QUIC_STATUS_SUCCESS")));
        Assert.AreEqual(LogLevel.Trace,
            Http3OriginClientSession.ClassifyShutdownLevel(new OperationCanceledException()));
        Assert.AreEqual(LogLevel.Trace, Http3OriginClientSession.ClassifyShutdownLevel(
            new IOException("wrapped", new QuicException(QuicError.ConnectionAborted, null, "peer"))));
    }

    [TestMethod]
    public void QuicUnknownFailure_StaysDebug()
    {
        Assert.AreEqual(LogLevel.Debug, Http3OriginClientSession.ClassifyShutdownLevel(
            new InvalidOperationException("genuinely unexpected")));
        Assert.AreEqual(LogLevel.Debug, Http3OriginClientSession.ClassifyShutdownLevel(
            new QuicException(QuicError.InternalError, null, "something else")));
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void AfterResponseCancellation_IsBenign_ButHandlerFaultStaysError()
    {
        // "HTTP/2 AfterResponse handler failed" with TaskCanceledException (1).
        var benign = new ProxyHttpException("HTTP/2 AfterResponse handler failed", new TaskCanceledException(), null);
        Assert.IsTrue(ProxyDiagnostics.IsExpected(benign));

        var fault = new ProxyHttpException("HTTP/2 AfterResponse handler failed",
            new InvalidOperationException("user handler bug"), null);
        Assert.IsFalse(ProxyDiagnostics.IsExpected(fault));
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void BenignSessionLogSignatures_NeverRiseToError()
    {
        var benign = new (string Name, Exception Ex)[]
        {
            ("read reset 10054", new IOException("Unable to read data from the transport connection: " +
                "An existing connection was forcibly closed by the remote host.",
                new SocketException((int)SocketError.ConnectionReset))),
            ("read abort 10053", new IOException("Unable to read data from the transport connection: " +
                "An established connection was aborted by the software in your host machine.",
                new SocketException((int)SocketError.ConnectionAborted))),
            ("write abort 10053", new IOException("Unable to write data to the transport connection.",
                new SocketException((int)SocketError.ConnectionAborted))),
            ("NullOriginStream cancel", new TaskCanceledException()),
            ("dns 11001", new SocketException((int)SocketError.HostNotFound)),
            ("dns nodata 11004", new SocketException((int)SocketError.NoData)),
            ("tls abort", new AuthenticationException("The remote party closed the transport stream.")),
            ("handshake eof", new ProxyConnectException("Couldn't authenticate host 'h'.",
                new IOException("Received an unexpected EOF or 0 bytes from the transport stream."), null!)),
        };

        foreach (var (name, ex) in benign)
        {
            var log = new CapturingLogger();
            ProxyDiagnostics.ReportException(log, name, ex);
            Assert.IsFalse(log.HasLevel(LogLevel.Error), $"{name} must not log at Error");
            Assert.IsFalse(log.HasLevel(LogLevel.Warning), $"{name} must not log at Warning");
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public LogLevel Minimum { get; set; } = LogLevel.Trace;
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public bool HasLevel(LogLevel level) => Entries.Exists(e => e.Level == level);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= Minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
