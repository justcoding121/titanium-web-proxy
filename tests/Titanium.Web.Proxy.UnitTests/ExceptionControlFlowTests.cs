using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Characterization for issue #634: reserve exceptions for exceptional paths, treat
///     cancellation / stale-connection retries as expected, and keep malformed framing strict.
/// </summary>
[TestClass]
public class ExceptionControlFlowTests
{
    [TestMethod]
    public void ProxyDiagnostics_OperationCanceledException_IsExpected_NotError()
    {
        var capturing = new CapturingLogger();
        var oce = new OperationCanceledException("Session was terminated by user.");

        Assert.IsTrue(ProxyDiagnostics.IsExpected(oce));
        ProxyDiagnostics.ReportException(capturing, "Client session cancelled", oce);

        Assert.AreEqual(0, capturing.ErrorCount,
            "user cancellation must not be logged at Error");
        Assert.IsTrue(capturing.DebugCount >= 1);
    }

    [TestMethod]
    public void ProxyDiagnostics_RetryableServerConnectionException_IsExpected()
    {
        var ex = new RetryableServerConnectionException(
            "Server connection was closed before any response was received.");
        Assert.IsTrue(ProxyDiagnostics.IsExpected(ex));

        var wrapped = new ProxyHttpException("wrapped", ex, null);
        Assert.IsTrue(ProxyDiagnostics.IsExpected(wrapped),
            "typed retryable failures must stay expected when nested");
    }

    [TestMethod]
    public void ProxyDiagnostics_IoExceptionWrappedAsProxyHttpException_IsExpected_NotError()
    {
        // H2→H3 / H2→H1 bridge idle teardown after a browsing pause: QuicException derives from
        // IOException and used to be forced through ReportUnexpected (red Error). Classification
        // must keep nested transport failures at Debug.
        var capturing = new CapturingLogger();
        var wrapped = new ProxyHttpException(
            "H2→H3 bridge origin round trip failed for stream 1",
            new IOException("The connection timed out from inactivity."),
            null);

        Assert.IsTrue(ProxyDiagnostics.IsExpected(wrapped));
        ProxyDiagnostics.ReportException(capturing, wrapped.Message, wrapped);

        Assert.AreEqual(0, capturing.ErrorCount,
            "idle/peer-close transport failures must not be logged at Error");
        Assert.IsTrue(capturing.DebugCount >= 1);
    }

    [TestMethod]
    public void ProxyDiagnostics_ProtocolProxyHttpException_RemainsUnexpected()
    {
        var capturing = new CapturingLogger();
        var ex = new ProxyHttpException(
            "HTTP/2 protocol error: expected a SETTINGS frame immediately after the connection preface, got Data.",
            null, null);

        Assert.IsFalse(ProxyDiagnostics.IsExpected(ex));
        ProxyDiagnostics.ReportException(capturing, ex.Message, ex);

        Assert.AreEqual(1, capturing.ErrorCount,
            "genuine protocol violations must still surface at Error");
    }

    [TestMethod]
    public void ProxyDiagnostics_ProxyConnectException_FromAbortedTls_IsExpected_NotError()
    {
        // 12-minute idle repro: Edge/OS background CONNECT + MITM handshake abort surfaces as
        // ProxyConnectException("Couldn't authenticate host…", AuthenticationException) and was
        // logged red via catch (ProxyException) → OnException → ReportException.
        var capturing = new CapturingLogger();
        var auth = new AuthenticationException("Authentication failed, see inner exception.");
        var connect = new ProxyConnectException(
            "Couldn't authenticate host 'login.live.com' with certificate 'login.live.com'.", auth,
            session: null!);

        Assert.IsTrue(ProxyDiagnostics.IsExpected(connect));
        ProxyDiagnostics.ReportException(capturing, "Unhandled exception in proxy", connect);

        Assert.AreEqual(0, capturing.ErrorCount,
            "aborted client TLS during CONNECT must not be logged at Error");
        Assert.IsTrue(capturing.DebugCount >= 1);
    }

    [TestMethod]
    public void ProxyDiagnostics_ProxyConnectException_PolicyFailure_RemainsUnexpected()
    {
        var capturing = new CapturingLogger();
        var connect = new ProxyConnectException(
            "UpstreamHttpProtocol.Http2 was required but the origin did not negotiate HTTP/2.",
            new NotSupportedException("Origin does not support HTTP/2."),
            session: null!);

        Assert.IsFalse(ProxyDiagnostics.IsExpected(connect));
        ProxyDiagnostics.ReportException(capturing, "Unhandled exception in proxy", connect);

        Assert.AreEqual(1, capturing.ErrorCount,
            "unsatisfiable upstream protocol policy must still surface at Error");
    }

    [TestMethod]
    public void ProxyDiagnostics_MalformedProtocolProxyHttpException_IsNotExpected()
    {
        var ex = new ProxyHttpException("Invalid chunk length: 'ZZ'", null, null);
        Assert.IsFalse(ProxyDiagnostics.IsExpected(ex));
    }

    [TestMethod]
    public void ProxyDiagnostics_ObjectDisposedException_IsExpected()
    {
        Assert.IsTrue(ProxyDiagnostics.IsExpected(new ObjectDisposedException("stream")));
    }

    [TestMethod]
    public void ProxyDiagnostics_ProxyTimeoutException_IsExpected()
    {
        var ex = new ProxyTimeoutException("connect timed out", ProxyTimeoutKind.Connect);
        Assert.IsTrue(ProxyDiagnostics.IsExpected(ex));
    }

    [TestMethod]
    public void ProxyDiagnostics_ReportTrace_LogsAtTrace_WhenEnabled()
    {
        var capturing = new CapturingLogger();
        ProxyDiagnostics.ReportTrace(capturing, "low-level trace");

        Assert.AreEqual(1, capturing.TraceCount);
        StringAssert.Contains(capturing.LastMessage!, "low-level trace");
    }

    [TestMethod]
    public void ProxyDiagnostics_ReportCaught_LogsAtDebug_NotError()
    {
        var capturing = new CapturingLogger();
        var ex = new InvalidOperationException("intermediate hop");

        ProxyDiagnostics.ReportCaught(capturing, "RequestHandler session failed; rethrowing", ex);

        Assert.AreEqual(0, capturing.ErrorCount);
        Assert.AreEqual(1, capturing.DebugCount);
        Assert.AreSame(ex, capturing.Entries[0].Exception);
        StringAssert.Contains(capturing.LastMessage!, "RequestHandler session failed; rethrowing");
    }

    [TestMethod]
    public void ProxyDiagnostics_ReportCaught_DoesNotLog_WhenDebugDisabled()
    {
        var capturing = new CapturingLogger { EnabledMinimum = LogLevel.Error };
        var ex = new InvalidOperationException("should not format");

        ProxyDiagnostics.ReportCaught(capturing, "RetryPolicy caught candidate for retry", ex);

        Assert.AreEqual(0, capturing.DebugCount);
        Assert.AreEqual(0, capturing.ErrorCount);
        Assert.AreEqual(0, capturing.Entries.Count);
        Assert.AreEqual(0, capturing.LogCallCount);
    }

    [TestMethod]
    public void ProxyDiagnostics_ReportCritical_WithAndWithoutException()
    {
        var capturing = new CapturingLogger();
        ProxyDiagnostics.ReportCritical(capturing, "subsystem halted");
        ProxyDiagnostics.ReportCritical(capturing, "subsystem fault", new InvalidOperationException("root"));

        Assert.AreEqual(2, capturing.CriticalCount);
        Assert.IsNull(capturing.Entries[0].Exception);
        Assert.IsInstanceOfType<InvalidOperationException>(capturing.Entries[1].Exception);
    }

    [TestMethod]
    public void ProxyDiagnostics_ReportWarning_And_ReportInformation()
    {
        var capturing = new CapturingLogger();
        ProxyDiagnostics.ReportWarning(capturing, "undisposed warning");
        ProxyDiagnostics.ReportInformation(capturing, "startup milestone");

        Assert.AreEqual(1, capturing.WarningCount);
        Assert.AreEqual(1, capturing.InformationCount);
    }

    [TestMethod]
    public void ProxyDiagnostics_ReportUndisposedFinalizer_UsesSuppliedLogger()
    {
        var capturing = new CapturingLogger();
        ProxyDiagnostics.ReportUndisposedFinalizer(capturing, "CopyStream");

        Assert.AreEqual(1, capturing.WarningCount);
        StringAssert.Contains(capturing.LastMessage!, "CopyStream was finalized without being disposed first.");
    }

    [TestMethod]
    public void ProxyDiagnostics_ReportUndisposedFinalizer_FallsBackToProcessLogger()
    {
        var capturing = new CapturingLogger();
        var previous = ProxyDiagnostics.Logger;
        try
        {
            ProxyDiagnostics.Logger = capturing;
            ProxyDiagnostics.ReportUndisposedFinalizer(null, "BufferPool");
        }
        finally
        {
            ProxyDiagnostics.Logger = previous;
        }

        Assert.AreEqual(1, capturing.WarningCount);
        StringAssert.Contains(capturing.LastMessage!, "BufferPool was finalized without being disposed first.");
    }

    [TestMethod]
    public async Task ReadResponseStatus_EofBeforeStatus_ReturnsNull()
    {
        using var stream = CreateServerStream(Array.Empty<byte>());
        var status = await stream.ReadResponseStatus(CancellationToken.None);
        Assert.IsNull(status);
    }

    [TestMethod]
    public async Task ReadResponseStatus_BlankLineThenEof_ReturnsNull()
    {
        using var stream = CreateServerStream(Encoding.ASCII.GetBytes("\r\n"));
        var status = await stream.ReadResponseStatus(CancellationToken.None);
        Assert.IsNull(status);
    }

    [TestMethod]
    public async Task ReadResponseStatus_ValidStatusLine_ReturnsParsedInfo()
    {
        using var stream = CreateServerStream(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n"));
        var status = await stream.ReadResponseStatus(CancellationToken.None);
        Assert.IsNotNull(status);
        Assert.AreEqual(200, status.Value.StatusCode);
        Assert.AreEqual("OK", status.Value.Description);
        Assert.AreEqual(HttpHeader.Version11, status.Value.Version);
    }

    [TestMethod]
    public async Task ReadResponseStatus_MalformedStatusLine_StillThrows()
    {
        using var stream = CreateServerStream(Encoding.ASCII.GetBytes("NOT-A-STATUS\r\n"));
        await Assert.ThrowsExactlyAsync<FormatException>(async () =>
            await stream.ReadResponseStatus(CancellationToken.None));
    }

    private static HttpServerStream CreateServerStream(byte[] payload)
    {
        return new HttpServerStream(
            new ProxyServer(false, false, false),
            new MemoryStream(payload),
            new DefaultBufferPool(),
            CancellationToken.None);
    }

    private sealed class CapturingLogger : ILogger
    {
        public int ErrorCount { get; private set; }
        public int DebugCount { get; private set; }
        public int TraceCount { get; private set; }
        public int WarningCount { get; private set; }
        public int InformationCount { get; private set; }
        public int CriticalCount { get; private set; }
        public int LogCallCount { get; private set; }
        public string? LastMessage { get; private set; }
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        /// <summary>
        ///     Minimum level for which <see cref="IsEnabled" /> returns true. Defaults to Trace so all
        ///     levels are enabled; raise to Error to verify Debug-gated methods become no-ops.
        /// </summary>
        public LogLevel EnabledMinimum { get; set; } = LogLevel.Trace;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= EnabledMinimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LogCallCount++;
            LastMessage = formatter(state, exception);
            Entries.Add((logLevel, exception));
            switch (logLevel)
            {
                case LogLevel.Trace: TraceCount++; break;
                case LogLevel.Debug: DebugCount++; break;
                case LogLevel.Information: InformationCount++; break;
                case LogLevel.Warning: WarningCount++; break;
                case LogLevel.Error: ErrorCount++; break;
                case LogLevel.Critical: CriticalCount++; break;
            }
        }
    }
}
