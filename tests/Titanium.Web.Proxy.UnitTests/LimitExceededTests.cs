using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Streams;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class LimitExceededTests
{
    [TestMethod]
    public void LimitExceeded_Enforce_LogsError_WithPropertyCliKeyAndHostOnly()
    {
        LimitLogThrottle.Reset();
        var capturing = new CapturingLogger(LogLevel.Trace);

        ProxyLog.LimitExceeded(capturing, LimitId.BufferedBody, PolicyMode.Enforce, 5000, 4096, "413", "cdn.example");

        Assert.AreEqual(1, capturing.Entries.Count);
        Assert.AreEqual(LogLevel.Error, capturing.Entries[0].Level);
        var text = capturing.Entries[0].Message;
        StringAssert.Contains(text, "MaxBufferedBodyBytes");
        StringAssert.Contains(text, "server.limits.maxBufferedBodyBytes");
        StringAssert.Contains(text, "413");
        StringAssert.Contains(text, "host cdn.example");
        StringAssert.Contains(text, "5000");
        Assert.IsFalse(text.Contains("http://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(text.Contains("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void LimitExceeded_Observe_LogsDebug()
    {
        LimitLogThrottle.Reset();
        var capturing = new CapturingLogger(LogLevel.Trace);

        ProxyLog.LimitExceeded(capturing, LimitId.HeaderCount, PolicyMode.Observe, 300, 256, "passed", null);

        Assert.AreEqual(1, capturing.Entries.Count);
        Assert.AreEqual(LogLevel.Debug, capturing.Entries[0].Level);
        StringAssert.Contains(capturing.Entries[0].Message, "server.policyModes.headerLimits");
    }

    [TestMethod]
    public void LimitExceeded_Flood_LogsOnce_AndMetricsCountEveryCall()
    {
        LimitLogThrottle.Reset();
        var capturing = new CapturingLogger(LogLevel.Error);
        long rejected = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ProxyMetricsMeter && instrument.Name == "twp.streams.rejected")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
            Interlocked.Add(ref rejected, measurement));
        listener.Start();

        const int calls = 10_000;
        for (var i = 0; i < calls; i++)
            ProxyLog.LimitExceeded(capturing, LimitId.Http3FramePayload, PolicyMode.Enforce, i, 4, "HTTP/3 stream reset");

        Assert.IsTrue(capturing.Entries.Count is > 0 and < 5,
            $"expected a bounded log, got {capturing.Entries.Count}");
        Assert.IsTrue(rejected >= calls, $"metrics counted {rejected}, expected at least {calls}");
        var joined = string.Join('\n', capturing.Entries.Select(e => e.Message));
        Assert.IsFalse(joined.Contains("http://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(joined.Contains("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PolicyExceptions_AreNotExpectedClientDisconnects()
    {
        Assert.IsFalse(ProxyDiagnostics.IsExpected(new BodySizeLimitExceededException("body")));
        Assert.IsFalse(ProxyDiagnostics.IsExpected(new Http2HeaderListTooLargeException("headers")));
        Assert.IsTrue(ProxyDiagnostics.IsExpected(new IOException("client gone")));
    }

    [TestMethod]
    public void NewHatches_DefaultToTodaysConstants_AndRejectHostileCeilings()
    {
        var limits = ProxyResourceLimits.Default;
        Assert.AreEqual(4L * 1024 * 1024, limits.MaxHttp3FramePayloadBytes);
        Assert.AreEqual((4 * 1024 * 1024) + (16 * 1024), limits.MaxDeferredOutboundBytesPerStream);
        Assert.AreEqual(100, limits.MaxTrailerHeaderCount);
        Assert.AreEqual(16 * 1024, limits.MaxTrailerHeaderBlockBytes);
        Assert.AreEqual(256 * 1024, limits.MaxHttp2CompressedHeaderBlockBytes);
        Assert.AreEqual(20, limits.MaxInterimResponses);
        Assert.AreEqual(3, limits.MaxAuthChallengeRounds);
        Assert.AreEqual(5, limits.MaxUpstreamProxyAuthenticationAttempts);
        Assert.AreEqual(12288, limits.MaxWinAuthTokenBytes);
        Assert.AreEqual(60, limits.Http2WindowUpdateTimeoutSeconds);
        Assert.AreEqual(1000, limits.MaxConcurrentStreamsPerConnection);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            limits.WithMaxHttp3FramePayloadBytes(ProxyResourceLimits.MaxHttp3FramePayloadCeiling + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            limits.WithMaxDeferredOutboundBytesPerStream(ProxyResourceLimits.MaxDeferredOutboundBytesCeiling + 1));
        var raised = limits.WithMaxHttp3FramePayloadBytes(8L * 1024 * 1024);
        Assert.AreEqual(8L * 1024 * 1024, raised.MaxHttp3FramePayloadBytes);
        Assert.AreEqual(limits.MaxDeferredOutboundBytesPerStream, raised.MaxDeferredOutboundBytesPerStream);
    }

    private const string ProxyMetricsMeter = "Titanium.Web.Proxy";

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        private readonly LogLevel minimumLevel;

        public CapturingLogger(LogLevel minimumLevel) => this.minimumLevel = minimumLevel;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
