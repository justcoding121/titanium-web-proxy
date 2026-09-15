using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Http;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Reflection coverage for ApplyRequest/ResponseBodyCapture and the Firefox trust background lane.
/// </summary>
[TestClass]
public class InterceptionBodyCaptureAndTrustBgCoverageTests
{
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [TestMethod]
    public void ApplyRequestBodyCapture_CoversCompleteTruncatedNoneAndNotCaptured()
    {
        var apply = typeof(InterceptionService).GetMethod("ApplyRequestBodyCapture", PrivateStatic)!;
        var snap = new SessionSnapshot();

        var complete = new Request { Method = "POST" };
        complete.ContentLength = 4;
        complete.IsBodyRead = true;
        apply.Invoke(null, [snap, complete, "abcd"u8.ToArray()]);
        Assert.AreEqual(BodyCaptureState.Complete, snap.RequestBodyCapture);
        Assert.AreEqual(4L, snap.RequestBodyOriginalSize);

        var truncated = new Request { Method = "POST" };
        truncated.IsBodyRead = true;
        var big = new byte[InterceptionService.MaxBodyBytes + 1];
        apply.Invoke(null, [snap, truncated, big]);
        Assert.AreEqual(BodyCaptureState.Truncated, snap.RequestBodyCapture);
        Assert.AreEqual(big.LongLength, snap.RequestBodyOriginalSize);

        var noBody = new Request { Method = "GET" };
        noBody.ContentLength = 0;
        apply.Invoke(null, [snap, noBody, null]);
        Assert.AreEqual(BodyCaptureState.None, snap.RequestBodyCapture);

        var huge = new Request { Method = "POST" };
        huge.ContentLength = InspectorBodyLimits.MaxMapLocalFileBytes + 1;
        apply.Invoke(null, [snap, huge, null]);
        Assert.AreEqual(BodyCaptureState.NotCaptured, snap.RequestBodyCapture);
        Assert.AreEqual(huge.ContentLength, snap.RequestBodyOriginalSize);

        var unreadFinite = new Request { Method = "POST" };
        unreadFinite.ContentLength = 128;
        unreadFinite.IsBodyRead = false;
        apply.Invoke(null, [snap, unreadFinite, null]);
        Assert.AreEqual(BodyCaptureState.None, snap.RequestBodyCapture);
    }

    [TestMethod]
    public void ApplyResponseBodyCapture_CoversAllCaptureStates()
    {
        var apply = typeof(InterceptionService).GetMethod("ApplyResponseBodyCapture", PrivateStatic)!;
        var snap = new SessionSnapshot();
        var req = new Request { Method = "GET" };

        var complete = new Response { StatusCode = 200 };
        complete.ContentLength = 2;
        complete.IsBodyRead = true;
        apply.Invoke(null, [snap, complete, req, "ok"u8.ToArray()]);
        Assert.AreEqual(BodyCaptureState.Complete, snap.ResponseBodyCapture);
        Assert.AreEqual(2L, snap.BodySize);

        var truncated = new Response { StatusCode = 200 };
        truncated.IsBodyRead = true;
        var big = new byte[InterceptionService.MaxBodyBytes + 1];
        apply.Invoke(null, [snap, truncated, req, big]);
        Assert.AreEqual(BodyCaptureState.Truncated, snap.ResponseBodyCapture);

        var wsReq = new Request { Method = "GET" };
        wsReq.Headers.AddHeader("Upgrade", "websocket");
        var wsResp = new Response { StatusCode = 101 };
        apply.Invoke(null, [snap, wsResp, wsReq, null]);
        Assert.AreEqual(BodyCaptureState.None, snap.ResponseBodyCapture);
        Assert.IsTrue(wsReq.UpgradeToWebSocket);

        snap = new SessionSnapshot { IsServerSentEvents = true };
        var sse = new Response { StatusCode = 200, ContentType = "text/event-stream" };
        sse.ContentLength = -1;
        apply.Invoke(null, [snap, sse, req, null]);
        Assert.AreEqual(BodyCaptureState.Streaming, snap.ResponseBodyCapture);
        Assert.IsTrue(snap.ResponseBodyStreamOpen);
        Assert.IsTrue(snap.IsServerSentEvents);

        snap = new SessionSnapshot();
        var sseCt = new Response { StatusCode = 200, ContentType = "text/event-stream" };
        apply.Invoke(null, [snap, sseCt, req, null]);
        Assert.IsTrue(snap.IsServerSentEvents);

        snap = new SessionSnapshot();
        var huge = new Response { StatusCode = 200 };
        huge.ContentLength = InspectorBodyLimits.MaxMapLocalFileBytes + 1;
        apply.Invoke(null, [snap, huge, req, null]);
        Assert.AreEqual(BodyCaptureState.NotCaptured, snap.ResponseBodyCapture);
        Assert.AreEqual(huge.ContentLength, snap.BodySize);

        snap = new SessionSnapshot();
        var unread = new Response { StatusCode = 200 };
        unread.ContentLength = 64;
        unread.IsBodyRead = false;
        apply.Invoke(null, [snap, unread, req, null]);
        Assert.AreEqual(BodyCaptureState.NotCaptured, snap.ResponseBodyCapture);

        snap = new SessionSnapshot();
        var empty = new Response { StatusCode = 204 };
        apply.Invoke(null, [snap, empty, req, null]);
        Assert.AreEqual(BodyCaptureState.None, snap.ResponseBodyCapture);
    }

    [TestMethod]
    public void FinalizeStreamingBody_AndPublishTeePreview_CoverStreamingEnds()
    {
        var finalize = typeof(InterceptionService).GetMethod("FinalizeStreamingBody", PrivateStatic)!;
        var publish = typeof(InterceptionService).GetMethod("PublishTeePreview", PrivateStatic)!;

        var nonStreaming = new SessionSnapshot { ResponseBodyCapture = BodyCaptureState.Complete };
        nonStreaming.ResponseTeeStream = new MemoryStream("x"u8.ToArray());
        finalize.Invoke(null, [nonStreaming]);
        Assert.IsNull(nonStreaming.ResponseTeeStream);

        var streaming = new SessionSnapshot
        {
            ResponseBodyCapture = BodyCaptureState.Streaming,
            IsServerSentEvents = true,
            ResponseBytesSeen = 12,
        };
        streaming.ResponseTeeStream = new MemoryStream(Encoding.UTF8.GetBytes("data: hi\n\n"));
        finalize.Invoke(null, [streaming]);
        Assert.AreEqual(BodyCaptureState.Complete, streaming.ResponseBodyCapture);
        Assert.IsFalse(streaming.ResponseBodyStreamOpen);
        Assert.IsNotNull(streaming.SseEvents);
        Assert.IsNull(streaming.ResponseTeeStream);

        var truncated = new SessionSnapshot
        {
            ResponseBodyCapture = BodyCaptureState.Streaming,
            ResponseBytesSeen = InterceptionService.MaxBodyBytes + 50,
        };
        var tee = new MemoryStream();
        tee.Write(new byte[InterceptionService.MaxBodyBytes]);
        truncated.ResponseTeeStream = tee;
        truncated.ResponseBodyBytes = tee.ToArray();
        finalize.Invoke(null, [truncated]);
        Assert.AreEqual(BodyCaptureState.Truncated, truncated.ResponseBodyCapture);

        var emptyTee = new SessionSnapshot();
        publish.Invoke(null, [emptyTee]);
        Assert.IsNull(emptyTee.ResponseBodyBytes);
    }

    [TestMethod]
    public async Task FirefoxTrustBackground_ScheduleCoalesceDropWaitAndTimeout()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };

        await interception.WaitForFirefoxTrustBackgroundIdleAsync();
        interception.DropPendingFirefoxTrustBackgroundWork();

        interception.ScheduleClearPendingFirefoxRootTrust();
        interception.ScheduleClearPendingFirefoxRootTrust(); // coalesce same kind
        interception.ScheduleFirefoxEnterpriseRootsBestEffort();
        await interception.WaitForFirefoxTrustBackgroundIdleAsync();

        var enqueue = typeof(InterceptionService).GetMethod("EnqueueFirefoxTrustBackground", PrivateInstance)!;
        var kindType = typeof(InterceptionService).GetNestedType("FirefoxTrustBgKind", BindingFlags.NonPublic)!;
        var clearKind = Enum.Parse(kindType, "Clear");
        var pruneKind = Enum.Parse(kindType, "Prune");
        var enableKind = Enum.Parse(kindType, "Enable");

        var ranPrune = 0;
        enqueue.Invoke(interception, [pruneKind, (Action)(() => Interlocked.Increment(ref ranPrune))]);
        enqueue.Invoke(interception, [pruneKind, (Action)(() => Interlocked.Increment(ref ranPrune))]); // coalesce
        enqueue.Invoke(interception, [pruneKind, (Action)(() => throw new InvalidOperationException("prune-fail"))]);
        await interception.WaitForFirefoxTrustBackgroundIdleAsync();
        Assert.IsTrue(ranPrune >= 1);

        var started = new ManualResetEventSlim(false);
        enqueue.Invoke(interception, [clearKind, (Action)(() =>
        {
            started.Set();
            Thread.Sleep(4000); // exceeds 3s Clear/Enable timeout
        })]);
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(2)));
        enqueue.Invoke(interception, [enableKind, (Action)(() => { })]);
        interception.DropPendingFirefoxTrustBackgroundWork();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        try
        {
            await interception.WaitForFirefoxTrustBackgroundIdleAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when a Clear job is still draining past the wait token.
        }

        await interception.WaitForFirefoxTrustBackgroundIdleAsync();
        interception.SchedulePruneOrphanedPersonalCertificates(false); // no-op under UseInMemoryTrustState
    }
}
