using System;
using System.Collections.Generic;
using System.IO;
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
    public void ProxyDiagnostics_ReportCritical_WithAndWithoutException()
    {
        var capturing = new CapturingLogger();
        ProxyDiagnostics.ReportCritical(capturing, "subsystem halted");
        ProxyDiagnostics.ReportCritical(capturing, "subsystem fault", new InvalidOperationException("root"));

        Assert.AreEqual(2, capturing.CriticalCount);
        Assert.IsNull(capturing.Entries[0].Exception);
        Assert.IsInstanceOfType(capturing.Entries[1].Exception, typeof(InvalidOperationException));
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
        public string? LastMessage { get; private set; }
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
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
