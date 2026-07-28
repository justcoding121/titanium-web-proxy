using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit tests covering:
///     <list type="bullet">
///       <item>HTTP/3 request timing — milestone ordering and <c>CompletedAt</c> is always set.</item>
///       <item>Upstream proxy chain — <see cref="QuicProxyNotSupportedException" /> shape and message.</item>
///     </list>
/// </summary>
[TestClass]
public class Http3TimingAndProxyChainTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // HttpRequestTiming milestones
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Timing_MarkComplete_SetsCompletedAtAndIsComplete()
    {
        var t = new HttpRequestTiming(DateTime.UtcNow);
        Assert.IsNull(t.CompletedAt);
        Assert.IsFalse(t.IsComplete);

        t.MarkComplete();

        Assert.IsNotNull(t.CompletedAt);
        Assert.IsTrue(t.IsComplete);
    }

    [TestMethod]
    public void Timing_MarkComplete_IsIdempotent()
    {
        var t = new HttpRequestTiming(DateTime.UtcNow);
        t.MarkComplete();
        var first = t.CompletedAt;

        // A second call must not overwrite CompletedAt.
        Thread.Sleep(5);
        t.MarkComplete();

        Assert.AreEqual(first, t.CompletedAt, "Second MarkComplete must not change CompletedAt.");
    }

    [TestMethod]
    public void Timing_MilestoneOrdering_IsChronological()
    {
        var t = new HttpRequestTiming(DateTime.UtcNow);

        Thread.Sleep(1);
        t.MarkRequestHeadersReceived();
        Thread.Sleep(1);
        t.MarkConnectionReady(Guid.NewGuid(), reused: false);
        Thread.Sleep(1);
        t.MarkRequestSent();
        Thread.Sleep(1);
        t.MarkResponseHeadersReceived();
        Thread.Sleep(1);
        t.MarkComplete();

        // Each milestone must be >= the previous one.
        Assert.IsTrue(t.SessionCreatedAt <= t.RequestHeadersReceivedAt!.Value,
            "SessionCreatedAt <= RequestHeadersReceivedAt");
        Assert.IsTrue(t.RequestHeadersReceivedAt.Value <= t.ConnectionReadyAt!.Value,
            "RequestHeadersReceivedAt <= ConnectionReadyAt");
        Assert.IsTrue(t.ConnectionReadyAt.Value <= t.RequestSentAt!.Value,
            "ConnectionReadyAt <= RequestSentAt");
        Assert.IsTrue(t.RequestSentAt.Value <= t.ResponseHeadersReceivedAt!.Value,
            "RequestSentAt <= ResponseHeadersReceivedAt");
        Assert.IsTrue(t.ResponseHeadersReceivedAt.Value <= t.CompletedAt!.Value,
            "ResponseHeadersReceivedAt <= CompletedAt");
    }

    [TestMethod]
    public void Timing_ConnectionReadyAt_NullForSyntheticResponse()
    {
        // Synthetic responses skip the origin bridge entirely — ConnectionReadyAt stays null.
        var t = new HttpRequestTiming(DateTime.UtcNow);
        t.MarkRequestHeadersReceived();
        t.MarkComplete();

        Assert.IsNull(t.ConnectionReadyAt, "ConnectionReadyAt must be null for synthetic responses.");
        Assert.IsNotNull(t.CompletedAt, "CompletedAt must always be set.");
    }

    [TestMethod]
    public void Timing_MarkConnectionReady_RecordsReusedFlag()
    {
        var t = new HttpRequestTiming(DateTime.UtcNow);
        var connId = Guid.NewGuid();

        t.MarkConnectionReady(connId, reused: true);

        Assert.IsTrue(t.UpstreamConnectionReused);
        Assert.AreEqual(connId, t.UpstreamConnectionId);
        Assert.AreEqual(1, t.AttemptCount);
    }

    [TestMethod]
    public void Timing_MarkConnectionReady_FirstUseNotReused()
    {
        var t = new HttpRequestTiming(DateTime.UtcNow);
        t.MarkConnectionReady(Guid.NewGuid(), reused: false);

        Assert.IsFalse(t.UpstreamConnectionReused);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QuicProxyNotSupportedException
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void QuicProxyNotSupportedException_MessageContainsProxyDescription()
    {
        var ex = new QuicProxyNotSupportedException("my-proxy:8080");

        StringAssert.Contains(ex.Message, "my-proxy:8080");
    }

    [TestMethod]
    public void QuicProxyNotSupportedException_IsInternalException()
    {
        // Verify the exception is not public (it is internal — the proxy catches it internally
        // and falls back to TCP rather than surfacing it to user code).
        var type = typeof(QuicProxyNotSupportedException);
        Assert.IsFalse(type.IsPublic,
            "QuicProxyNotSupportedException should be internal, not part of the public API.");
    }

    [TestMethod]
    public void QuicProxyNotSupportedException_DerivesFromException()
    {
        var ex = new QuicProxyNotSupportedException("test");
        Assert.IsInstanceOfType(ex, typeof(Exception));
    }
}
