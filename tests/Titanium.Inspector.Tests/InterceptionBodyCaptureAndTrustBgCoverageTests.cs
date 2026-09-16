using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;

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
        enqueue.Invoke(interception, [enableKind, (Action)(() => { /* separator so prune is not coalesced away */ })]);
        enqueue.Invoke(interception, [pruneKind, (Action)(() => Interlocked.Increment(ref ranPrune))]);
        enqueue.Invoke(interception, [pruneKind, (Action)(() => throw new InvalidOperationException("prune-fail"))]);
        await interception.WaitForFirefoxTrustBackgroundIdleAsync();
        Assert.IsTrue(ranPrune >= 1);

        var started = new ManualResetEventSlim(false);
        enqueue.Invoke(interception, [clearKind, (Action)(() =>
        {
            started.Set();
            using var hold = new ManualResetEventSlim(false);
            hold.Wait(TimeSpan.FromMilliseconds(4000)); // exceeds 3s Clear/Enable timeout
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

    [TestMethod]
    public async Task TrustStoreApis_InMemoryAndOsSeams_CoverInstallFinalizeListRemove()
    {
        // Before Start: _proxy null arms.
        using (var cold = new InterceptionService(new RecordingSystemProxyController()))
        {
            Assert.IsFalse(cold.InstallRootStoresOnly(false));
            Assert.IsFalse(cold.FinalizeTrustAfterStoreMutation(false));
            Assert.IsFalse(cold.FinalizeTrustAfterAdminInstall(false));
            cold.ApplyUnixSslTrustOnUi(false);
            cold.ApplyUnixUntrustOnUi();
            Assert.AreEqual(0, cold.ListRootThumbprintsToRemove(false).Count);
            cold.RemoveRootThumbprintOnUi(false, "deadbeef");
            cold.FinalizeAfterRootRemove(false);
            cold.RemoveOsRootStoreOnly(false);
            cold.SchedulePruneOrphanedPersonalCertificates(false);
        }

        using var inMemory = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        await inMemory.StartAsync(IPAddress.Loopback, 0);
        try
        {
            inMemory.FailNextUserTrustInstall = true;
            Assert.IsFalse(inMemory.InstallRootStoresOnly(false));
            Assert.AreEqual(CertificateOsTrustKind.Failed, inMemory.LastOsTrustResult!.Kind);

            Assert.IsTrue(inMemory.InstallRootStoresOnly(false));
            Assert.IsTrue(inMemory.IsRootTrusted);
            Assert.IsTrue(inMemory.FinalizeTrustAfterStoreMutation(false));
            Assert.IsTrue(inMemory.FinalizeTrustAfterAdminInstall(false));
            inMemory.ApplyUnixSslTrustOnUi(false); // no-op (in-memory / Windows)
            inMemory.ApplyUnixUntrustOnUi();
            Assert.AreEqual(0, inMemory.ListRootThumbprintsToRemove(false).Count);
            inMemory.RemoveRootThumbprintOnUi(false, "deadbeef");
            inMemory.FinalizeAfterRootRemove(false);
            Assert.IsFalse(inMemory.IsRootTrusted);
            inMemory.SchedulePruneOrphanedPersonalCertificates(false); // no-op in-memory

            var truncate = typeof(InterceptionService).GetMethod("TruncateTrustMsg", PrivateStatic)!;
            Assert.AreEqual("", (string)truncate.Invoke(null, [null])!);
            Assert.AreEqual("", (string)truncate.Invoke(null, [""])!);
            Assert.AreEqual("short", (string)truncate.Invoke(null, ["short"])!);
            var longMsg = new string('x', 200);
            var truncated = (string)truncate.Invoke(null, [longMsg])!;
            Assert.IsTrue(truncated.EndsWith("...", StringComparison.Ordinal));
            Assert.IsTrue(truncated.Length <= 123);

            var log = typeof(InterceptionService).GetMethod("LogTrustFirefoxOutcome", PrivateInstance)!;
            log.Invoke(inMemory, [CertificateOsTrustResult.Ok("ok")]);
            log.Invoke(inMemory, [CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, "nope")]);

            var ff = inMemory.TrustFirefox();
            Assert.IsTrue(ff.Succeeded);
        }
        finally
        {
            inMemory.EnsureShutdown();
        }

        // Real CertificateManager path (Root CryptUI still suppressed).
        using var live = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = false,
        };
        await live.StartAsync(IPAddress.Loopback, 0);
        try
        {
            Assert.IsFalse(live.SetSystemProxy(true, stillWanted: () => false));

            var added = live.InstallRootStoresOnly(false);
            Assert.IsNotNull(live.LastOsTrustResult);
            live.ApplyUnixSslTrustOnUi(false);
            if (OperatingSystem.IsWindows())
            {
                Assert.IsTrue(live.FinalizeTrustAfterStoreMutation(false, rootStoreAdded: true));
                Assert.IsTrue(live.IsRootTrusted);
                await live.WaitForFirefoxTrustBackgroundIdleAsync();
            }
            else
            {
                _ = live.FinalizeTrustAfterStoreMutation(false, rootStoreAdded: added);
            }

            live.SchedulePruneOrphanedPersonalCertificates(false);
            await live.WaitForFirefoxTrustBackgroundIdleAsync();

            _ = live.ListRootThumbprintsToRemove(false);
            live.RemoveRootThumbprintOnUi(false, live.RootCertificate?.Thumbprint ?? "deadbeef");
            live.FinalizeAfterRootRemove(false);
            live.ApplyUnixUntrustOnUi();
            live.RemoveOsRootStoreOnly(false);

            _ = live.FinalizeTrustAfterAdminInstall(false);
            _ = live.TrustFirefox(); // InterceptionService arms only; FirefoxCertificateTrust excluded
        }
        finally
        {
            live.EnsureShutdown();
        }
    }

    [TestMethod]
    public void TeeThrottleAndEndlessStream_CoverEarlyReturnsAndCoalesce()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        using var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        using var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Response.HttpVersion = session.HttpClient.Request.HttpVersion;

        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };

        var throttle = typeof(InterceptionService).GetMethod("OnResponseBodyWriteThrottle", PrivateInstance)!;
        var tee = typeof(InterceptionService).GetMethod("TeeResponseChunk", PrivateInstance)!;
        var looks = typeof(InterceptionService).GetMethod("LooksLikeEndlessStream", PrivateStatic)!;
        var shouldBuffer = typeof(InterceptionService).GetMethod("ShouldBufferBody", PrivateInstance)!;

        // Body already read → early return (no tee).
        session.HttpClient.Response.IsBodyRead = true;
        var writeArgs = new BeforeBodyWriteEventArgs(session, [1, 2, 3], isChunked: false, isLastChunk: false);
        ((Task)throttle.Invoke(interception, [interception, writeArgs])!).GetAwaiter().GetResult();

        session.HttpClient.Response.IsBodyRead = false;
        // Not in _live → early return.
        ((Task)throttle.Invoke(interception, [interception, writeArgs])!).GetAwaiter().GetResult();

        var snap = new SessionSnapshot
        {
            ResponseBodyCapture = BodyCaptureState.Complete, // not Streaming → throttle skips tee
        };
        var liveField = typeof(InterceptionService).GetField("_live", PrivateInstance)!;
        var live = (System.Collections.IDictionary)liveField.GetValue(interception)!;
        live[session.HttpClient] = snap;
        ((Task)throttle.Invoke(interception, [interception, writeArgs])!).GetAwaiter().GetResult();

        snap.ResponseBodyCapture = BodyCaptureState.Streaming;
        snap.ResponseBodyStreamOpen = true;
        var mid = new BeforeBodyWriteEventArgs(session, "hi"u8.ToArray(), isChunked: true, isLastChunk: false);
        tee.Invoke(interception, [snap, mid]);
        Assert.IsNotNull(snap.ResponseTeeStream);
        // Coalesce window: second chunk within TeeUiCoalesceMs returns early without PublishTeePreview bump.
        snap.LastTeeUiUtcTicks = DateTime.UtcNow.Ticks;
        tee.Invoke(interception, [snap, mid]);
        Assert.IsTrue(snap.ResponseBytesSeen >= 2);

        var last = new BeforeBodyWriteEventArgs(session, "!"u8.ToArray(), isChunked: true, isLastChunk: true);
        tee.Invoke(interception, [snap, last]);
        Assert.IsFalse(snap.ResponseBodyStreamOpen);

        session.HttpClient.Request.Headers.AddHeader("Upgrade", "websocket");
        Assert.IsTrue(session.HttpClient.Request.UpgradeToWebSocket);
        Assert.IsTrue((bool)looks.Invoke(null, [session.HttpClient.Request, session, true])!);
        Assert.IsFalse((bool)shouldBuffer.Invoke(interception, [session.HttpClient.Request, session, true])!);
    }
}
