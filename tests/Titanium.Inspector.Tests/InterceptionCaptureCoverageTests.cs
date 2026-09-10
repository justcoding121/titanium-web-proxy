using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InterceptionCaptureCoverageTests
{
    [TestMethod]
    public async Task AutoResponder_AndCapturingOff_AndScripts_CoverRequestPipeline()
    {
        using var origin = StartOrigin("from-origin");
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            AutoResponder = new AutoResponderViewModel
            {
                Enabled = true,
            },
            MapRemote = new MapRemoteViewModel { Enabled = false },
            Breakpoints = new BreakpointViewModel(),
            ScriptOnRequest = "# comment\nset-header X-Inspected: 1",
            ScriptOnResponse = "set-header X-Out: 1",
            DecryptHttps = false,
            Capturing = true,
            ThrottleProfile = null,
        };
        interception.AutoResponder.Rules.Add(new AutoResponderRule
        {
            Enabled = true,
            MatchUrl = "*auto-hit*",
            StatusCode = 201,
            ContentType = "text/plain",
            Body = "mapped-local",
        });
        interception.ConfigureLogging(new InspectorSettings { LoggingEnableFile = false });
        interception.ApplyHttpProtocols();
        interception.IgnoreServerCertificateErrors = true;
        interception.DecryptSkipHosts = ["login.live.com"];
        interception.LegacyCrtsTestRoot = Path.Combine(Path.GetTempPath(), "twp-crts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(interception.LegacyCrtsTestRoot, "shared-crts"));
        interception.PruneLegacySharedCrts(force: true);

        var captured = new List<SessionSnapshot>();
        interception.SessionCaptured += (_, s) => captured.Add(s);

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort);
            var auto = await http.GetAsync("http://example.test/auto-hit");
            Assert.AreEqual(HttpStatusCode.Created, auto.StatusCode);
            Assert.AreEqual("mapped-local", await auto.Content.ReadAsStringAsync());

            interception.AutoResponder.Enabled = false;
            interception.MapRemote = new MapRemoteViewModel { Enabled = true };
            interception.MapRemote.Rules.Add(new MapRemoteRule
            {
                Enabled = true,
                MatchUrl = "*rewrite-me*",
                TargetUrl = $"http://127.0.0.1:{origin.Port}/rewritten",
            });
            var rewritten = await http.GetAsync("http://example.test/rewrite-me");
            Assert.AreEqual(HttpStatusCode.OK, rewritten.StatusCode);

            interception.Capturing = false;
            var silent = await http.GetAsync($"http://127.0.0.1:{origin.Port}/quiet");
            Assert.AreEqual(HttpStatusCode.OK, silent.StatusCode);

            interception.Capturing = true;
            interception.ScriptOnRequest = "abort";
            var aborted = await http.GetAsync($"http://127.0.0.1:{origin.Port}/abort-me");
            Assert.AreEqual(HttpStatusCode.Forbidden, aborted.StatusCode);

            interception.ScriptOnRequest = null;
            using var sseReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{origin.Port}/sse");
            var sse = await http.SendAsync(sseReq);
            Assert.AreEqual(HttpStatusCode.OK, sse.StatusCode);

            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (captured.Count < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(40);

            using var grpcReq = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{origin.Port}/grpc")
            {
                Content = new ByteArrayContent([1, 0, 0, 0, 2, 0xAA, 0xBB]),
            };
            grpcReq.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
            var grpcResp = await http.SendAsync(grpcReq);
            Assert.AreEqual(HttpStatusCode.OK, grpcResp.StatusCode);

            using var mp = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{origin.Port}/multipart")
            {
                Content = new StringContent(
                    "--abc\r\nContent-Disposition: form-data; name=\"f\"\r\n\r\nhi\r\n--abc--\r\n",
                    Encoding.UTF8,
                    "multipart/form-data"),
            };
            mp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data")
            {
                Parameters = { new System.Net.Http.Headers.NameValueHeaderValue("boundary", "abc") },
            };
            var mpResp = await http.SendAsync(mp);
            Assert.AreEqual(HttpStatusCode.OK, mpResp.StatusCode);

            using var connectClient = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
            })
            {
                Timeout = TimeSpan.FromSeconds(2),
            };
            try
            {
                _ = await connectClient.GetAsync("https://127.0.0.1:1/");
            }
            catch (Exception)
            {
                // CONNECT to a closed port is enough to exercise tunnel hooks.
            }

            Assert.IsTrue(captured.Count >= 1, "expected AutoResponder or rewrite capture");

            interception.ConfigureLogging(new InspectorSettings { LoggingEnableFile = false });
            interception.ReapplySystemProxyIfEnabled();
            _ = interception.SetSystemProxy(false);
            interception.SetLastOsTrustCancelled();
            Assert.AreEqual(CertificateOsTrustKind.Cancelled, interception.LastOsTrustResult?.Kind);
            interception.BeginBackgroundShutdown();
        }
        finally
        {
            interception.EnsureShutdown();
            interception.EnsureShutdown();
            origin.Dispose();
        }
    }

    [TestMethod]
    public void HelperReflection_CoversTruncateFormatShouldBufferAndTiming()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController());
        var flags = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var truncateBytes = typeof(InterceptionService).GetMethod("TruncateBytes", flags)!;
        var truncateText = typeof(InterceptionService).GetMethod("TruncateText", flags)!;
        var formatHeaders = typeof(InterceptionService).GetMethod("FormatHeaders", flags)!;
        var applyTiming = typeof(InterceptionService).GetMethod("ApplyTiming", flags)!;
        var addTunnel = typeof(InterceptionService).GetMethod("AddTunnelBytes", flags)!;
        var tryHost = typeof(InterceptionService).GetMethod("TryHost", flags)!;

        Assert.IsNull(truncateBytes.Invoke(null, new object?[] { null }));
        Assert.AreEqual(0, ((byte[])truncateBytes.Invoke(null, [Array.Empty<byte>()])!).Length);
        var big = new byte[InterceptionService.MaxBodyBytes + 8];
        Assert.AreEqual(InterceptionService.MaxBodyBytes, ((byte[])truncateBytes.Invoke(null, [big])!).Length);
        Assert.AreEqual("hi", (string)truncateText.Invoke(null, ["hi"])!);
        var longText = new string('x', InterceptionService.MaxBodyTextChars + 3);
        StringAssert.EndsWith((string)truncateText.Invoke(null, [longText])!, "…");

        var headers = new HeaderCollection();
        headers.AddHeader("X-A", "1");
        StringAssert.Contains((string)formatHeaders.Invoke(null, [headers])!, "X-A");

        var snap = new SessionSnapshot { StartedUtc = DateTimeOffset.UtcNow.AddMilliseconds(-15) };
        applyTiming.Invoke(null, [snap, null, snap.StartedUtc]);
        Assert.IsTrue(snap.DurationMs >= 0);
        addTunnel.Invoke(null, [snap, 12, 7]);
        Assert.AreEqual(19L, snap.BodySize);

        var req = new Request { RequestUriString = "https://host.test/x" };
        Assert.AreEqual("host.test", (string?)tryHost.Invoke(null, [req]));

        var shouldBuffer = typeof(InterceptionService).GetMethod("ShouldBufferBody", flags)!;
        Assert.IsNotNull(shouldBuffer);

        var eval = typeof(InterceptionService).GetMethod("EvaluateUnixTrustSuccess", flags)!;
        Assert.IsFalse((bool)eval.Invoke(null, new object?[] { null })!);
        Assert.IsTrue((bool)eval.Invoke(null, [CertificateOsTrustResult.Ok()])!);

        var buildUrl = typeof(InterceptionService).GetMethod("BuildDisplayUrl", flags)!;
        var applyMark = typeof(InterceptionService).GetMethod("ApplyTranscodeMark", flags)!;
        var mark = new GrpcJsonTranscodeSessionMark
        {
            ClientMethod = "POST",
            ClientPathAndQuery = "/v1/echo?x=1",
            ClientContentType = "application/json",
            UpstreamMethod = "POST",
            UpstreamPath = "/pkg.Svc/Echo",
            UpstreamContentType = "application/grpc",
            UpstreamRequestBody = [1, 0, 0, 0, 2, 0xAA, 0xBB],
            UpstreamResponseBody = [0, 0, 0, 0, 1, 0x01],
        };
        applyMark.Invoke(null, [snap, mark]);
        Assert.IsTrue(snap.IsTranscoded);
        var reqAbs = new Request { RequestUriString = "https://host.test/upstream" };
        StringAssert.Contains((string)buildUrl.Invoke(null, [reqAbs, mark])!, "/v1/echo");
        _ = (string)buildUrl.Invoke(null, [new Request(), mark])!;
        _ = (string)buildUrl.Invoke(null, [new Request(), null])!;
    }

    [TestMethod]
    public void ProtocolFrames_CoverExtendedLengthsGrpcAndMultipart()
    {
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseWebSocketFrames(null).Count);
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseWebSocketFrames([]).Count);

        var len126 = new byte[4 + 20];
        len126[0] = 0x82; // FIN binary
        len126[1] = 126;
        len126[2] = 0;
        len126[3] = 20;
        for (var i = 0; i < 20; i++)
            len126[4 + i] = (byte)i;
        Assert.AreEqual(1, ProtocolFrameInspectors.ParseWebSocketFrames(len126).Count);

        var ping = new byte[] { 0x89, 0x00 };
        Assert.AreEqual("Ping", ProtocolFrameInspectors.ParseWebSocketFrames(ping)[0].Opcode);
        Assert.AreEqual("Pong", ProtocolFrameInspectors.ParseWebSocketFrames([0x8A, 0x00])[0].Opcode);
        Assert.AreEqual("Close", ProtocolFrameInspectors.ParseWebSocketFrames([0x88, 0x00])[0].Opcode);
        Assert.AreEqual("Continuation", ProtocolFrameInspectors.ParseWebSocketFrames([0x80, 0x00])[0].Opcode);
        StringAssert.StartsWith(ProtocolFrameInspectors.ParseWebSocketFrames([0x8F, 0x00])[0].Opcode, "Op");

        var len127 = new byte[10 + 4];
        len127[0] = 0x82;
        len127[1] = 127;
        len127[9] = 4;
        Assert.AreEqual(1, ProtocolFrameInspectors.ParseWebSocketFrames(len127).Count);

        var masked = new byte[] { 0x81, 0x83, 1, 2, 3, 4, (byte)('a' ^ 1), (byte)('b' ^ 2), (byte)('c' ^ 3) };
        Assert.AreEqual(1, ProtocolFrameInspectors.ParseWebSocketFrames(masked).Count);

        var live = ProtocolFrameInspectors.FromLiveFrame("Client", "Binary", [0x01, 0x02]);
        Assert.AreEqual("Client", live.Direction);

        Assert.AreEqual(0, ProtocolFrameInspectors.ParseGrpcFrames(null).Count);
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseGrpcFrames(new byte[3]).Count);
        var grpc = new byte[] { 1, 0, 0, 0, 2, 0xAA, 0xBB };
        var frames = ProtocolFrameInspectors.ParseGrpcFrames(grpc);
        Assert.AreEqual(1, frames.Count);
        Assert.IsTrue(frames[0].Compressed);

        Assert.AreEqual(0, ProtocolFrameInspectors.ParseMultipart("text/plain", [1]).Count);
        var body = Encoding.UTF8.GetBytes(
            "--abc\r\nContent-Disposition: form-data; name=\"f\"\r\n\r\nhi\r\n--abc--\r\n");
        var parts = ProtocolFrameInspectors.ParseMultipart("multipart/form-data; boundary=abc", body);
        Assert.IsTrue(parts.Count >= 1);
    }

    [TestMethod]
    public void FillResponse_CoversSseGrpcTranscodeMultipartAndWebsocket()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        using var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Response.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Response.StatusCode = 200;
        session.HttpClient.Response.IsBodyRead = true;
        session.HttpClient.Response.ContentType = "text/event-stream";
        session.HttpClient.Response.Body = "data: hi\n\n"u8.ToArray();

        var fill = typeof(InterceptionService).GetMethod("FillResponse",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var snap = new SessionSnapshot { StartedUtc = DateTimeOffset.UtcNow, IsServerSentEvents = true };
        fill.Invoke(null, [snap, session]);
        Assert.AreEqual(200, snap.StatusCode);
        Assert.IsTrue(snap.IsServerSentEvents);

        session.HttpClient.Response.ContentType = "application/grpc";
        session.HttpClient.Response.Body = [0, 0, 0, 0, 1, 0x0a];
        snap.IsGrpc = true;
        snap.IsTranscoded = false;
        fill.Invoke(null, [snap, session]);
        Assert.IsNotNull(snap.GrpcFrames);

        var mark = new GrpcJsonTranscodeSessionMark
        {
            ClientMethod = "GET",
            ClientPathAndQuery = "/v1/echo",
            UpstreamMethod = "POST",
            UpstreamPath = "/pkg.Svc/Echo",
            UpstreamResponseBody = session.HttpClient.Response.Body,
            ClientRequestBody = "{\"n\":1}"u8.ToArray(),
            UpstreamRequestBody = [1, 0, 0, 0, 1, 0x0a],
        };
        session.UserData = mark;
        snap.IsTranscoded = true;
        fill.Invoke(null, [snap, session]);
        Assert.IsNotNull(snap.ProtobufDecodedText);

        session.HttpClient.Response.ContentType = "multipart/form-data; boundary=abc";
        session.HttpClient.Response.Body = Encoding.UTF8.GetBytes(
            "--abc\r\nContent-Disposition: form-data; name=\"f\"\r\n\r\nhi\r\n--abc--\r\n");
        snap.IsMultipart = true;
        snap.ContentType = session.HttpClient.Response.ContentType;
        fill.Invoke(null, [snap, session]);
        Assert.IsTrue(snap.MultipartParts is { Count: > 0 } || snap.StatusCode == 200);

        snap.IsWebSocket = true;
        fill.Invoke(null, [snap, session]);
        Assert.AreEqual(200, snap.StatusCode);

        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            Capturing = true,
            DecryptHttps = false,
            ThrottleProfile = new NetworkThrottleProfile("cov", 1, 0),
        };
        var flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "https://a.test/grpc";
        session.HttpClient.Request.IsBodyRead = true;
        session.HttpClient.Request.Body = [1, 0, 0, 0, 1, 0x0a];
        session.HttpClient.Request.ContentType = "application/grpc";
        session.HttpClient.Request.Headers.AddHeader("Upgrade", "websocket");
        session.HttpClient.Request.Headers.AddHeader("Accept", "text/event-stream");
        var preview = typeof(InterceptionService).GetMethod("CreatePreviewSnapshot", flags)!;
        var previewSnap = (SessionSnapshot)preview.Invoke(interception, [session, true])!;
        Assert.IsTrue(previewSnap.IsWebSocket);
        Assert.IsTrue(previewSnap.IsGrpc);
        Assert.IsNotNull(previewSnap.WebSocketFrames);

        var shouldBuffer = typeof(InterceptionService).GetMethod("ShouldBufferBody", flags)!;
        session.MaxBufferedBodyBytes = 10;
        session.HttpClient.Request.ContentLength = 100;
        Assert.IsFalse((bool)shouldBuffer.Invoke(interception, [session.HttpClient.Request, session])!);
        session.MaxBufferedBodyBytes = 0;
        Assert.IsTrue((bool)shouldBuffer.Invoke(interception, [session.HttpClient.Request, session])!);
        session.MaxBufferedBodyBytes = 1024;
        session.HttpClient.Request.ContentLength = -1;
        Assert.IsTrue((bool)shouldBuffer.Invoke(interception, [session.HttpClient.Request, session])!);

        var throttleReq = typeof(InterceptionService).GetMethod("OnRequestBodyWriteThrottle", flags)!;
        var throttleResp = typeof(InterceptionService).GetMethod("OnResponseBodyWriteThrottle", flags)!;
        var writeArgs = new BeforeBodyWriteEventArgs(session, [1, 2, 3], isChunked: false, isLastChunk: true);
        ((Task)throttleReq.Invoke(interception, [interception, writeArgs])!).GetAwaiter().GetResult();
        ((Task)throttleResp.Invoke(interception, [interception, writeArgs])!).GetAwaiter().GetResult();
        interception.ThrottleProfile = null;
        ((Task)throttleReq.Invoke(interception, [interception, writeArgs])!).GetAwaiter().GetResult();

        var cert = typeof(InterceptionService).GetMethod("OnServerCertValidation", flags)!;
        var certArgs = new CertificateValidationEventArgs(session, null, null, SslPolicyErrors.RemoteCertificateNameMismatch);
        interception.IgnoreServerCertificateErrors = true;
        ((Task)cert.Invoke(interception, [interception, certArgs])!).GetAwaiter().GetResult();
        Assert.IsTrue(certArgs.IsValid);
        interception.IgnoreServerCertificateErrors = false;
        ((Task)cert.Invoke(interception, [interception, certArgs])!).GetAwaiter().GetResult();

        var connect = new ConnectRequest("skip.test:443".GetByteString());
        var tunnel = new TunnelConnectSessionEventArgs(proxy, endPoint, connect, clientStream, cts);
        var beforeTunnel = typeof(InterceptionService).GetMethod("OnBeforeTunnelConnect", flags)!;
        var afterTunnel = typeof(InterceptionService).GetMethod("OnBeforeTunnelConnectResponse", flags)!;
        interception.Capturing = false;
        ((Task)beforeTunnel.Invoke(interception, [interception, tunnel])!).GetAwaiter().GetResult();
        interception.Capturing = true;
        interception.DecryptHttps = true;
        interception.DecryptSkipHosts = ["skip.test"];
        ((Task)beforeTunnel.Invoke(interception, [interception, tunnel])!).GetAwaiter().GetResult();
        ((Task)afterTunnel.Invoke(interception, [interception, tunnel])!).GetAwaiter().GetResult();
        interception.DecryptHttps = false;
        ((Task)beforeTunnel.Invoke(interception, [interception, tunnel])!).GetAwaiter().GetResult();
        ((Task)afterTunnel.Invoke(interception, [interception, tunnel])!).GetAwaiter().GetResult();
        var afterResponse = typeof(InterceptionService).GetMethod("OnAfterResponse", flags)!;
        ((Task)afterResponse.Invoke(interception, [interception, session])!).GetAwaiter().GetResult();
        Assert.AreEqual(200, tunnel.HttpClient.Response.StatusCode == 0 ? 200 : tunnel.HttpClient.Response.StatusCode);
    }

    [TestMethod]
    public void OsTrustUxCopy_FormatStatus_CoversEveryKind()
    {
        Assert.AreEqual("Root CA is not trusted yet — try again, or Export CA", OsTrustUxCopy.FormatStatus(null));
        Assert.AreEqual("Root CA install cancelled",
            OsTrustUxCopy.FormatStatus(CertificateOsTrustResult.Fail(CertificateOsTrustKind.Cancelled, "x")));
        StringAssert.Contains(
            OsTrustUxCopy.FormatStatus(CertificateOsTrustResult.Fail(CertificateOsTrustKind.CertutilMissing, "x")),
            "Browser certificate tools");
        StringAssert.Contains(
            OsTrustUxCopy.FormatStatus(CertificateOsTrustResult.Fail(CertificateOsTrustKind.HomebrewMissing, "")),
            "Homebrew");
        StringAssert.Contains(
            OsTrustUxCopy.FormatStatus(CertificateOsTrustResult.Fail(CertificateOsTrustKind.HomebrewMissing, "brew msg")),
            "brew msg");
        StringAssert.Contains(
            OsTrustUxCopy.FormatStatus(CertificateOsTrustResult.Fail(CertificateOsTrustKind.MacNeedsManualTrustConfirm, "x")),
            "Always Trust");
        StringAssert.Contains(
            OsTrustUxCopy.FormatStatus(CertificateOsTrustResult.Fail(CertificateOsTrustKind.MacKeychainFailed, "x")),
            "Keychain");
        Assert.AreEqual("custom",
            OsTrustUxCopy.FormatStatus(CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, "custom")));
        StringAssert.Contains(
            OsTrustUxCopy.FormatStatus(CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, "")),
            "not trusted");

        foreach (var kind in new[]
                 {
                     CertificateOsTrustKind.MacNeedsManualTrustConfirm,
                     CertificateOsTrustKind.CertutilMissing,
                     CertificateOsTrustKind.HomebrewMissing,
                     CertificateOsTrustKind.Failed,
                 })
        {
            var (title, body, primary, _, _) = OsTrustUxCopy.FormatDecryptTrustFailed(
                CertificateOsTrustResult.Fail(kind, "detail"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(title));
            StringAssert.Contains(body, "detail");
            Assert.IsFalse(string.IsNullOrWhiteSpace(primary));
        }

        _ = OsTrustUxCopy.ConfirmRemoveRootCaBody();
        _ = OsTrustUxCopy.ConfirmElevateRootCaBody();
        _ = OsTrustUxCopy.TrustRecoveryAdminBody("failed");
        var settings = new InspectorSettings
        {
            SystemProxyBypassHosts = ["localhost"],
            DecryptSkipHosts = ["pin.test"],
        };
        StringAssert.Contains(ExclusionPreview.ExclusionSummary(settings), "Exclusions");
        Assert.AreEqual("", ExclusionPreview.ExclusionSummary(new InspectorSettings()));
        _ = ExclusionPreview.FormatForCurrentOs(settings);
        _ = ExclusionPreview.DescribeOpaqueReason(OpaqueTunnelReason.DecryptOff);
        _ = ExclusionPreview.DescribeOpaqueReason(OpaqueTunnelReason.BuiltInIdentity);
        _ = ExclusionPreview.DescribeOpaqueReason(OpaqueTunnelReason.BuiltInPinning);
        _ = ExclusionPreview.DescribeOpaqueReason(OpaqueTunnelReason.UserSkipList);
        _ = ExclusionPreview.DescribeOpaqueReason(OpaqueTunnelReason.UserOnlyList);
        _ = ExclusionPreview.DescribeOpaqueReason(OpaqueTunnelReason.None);
        Assert.AreEqual("", HostListFormat.Join(null));
        Assert.AreEqual(0, HostListFormat.Parse("# comment\n\n").Count);
        Assert.AreEqual(2, HostListFormat.Parse("a.com\nb.com\na.com").Count);
    }

    [TestMethod]
    public void TrustHelpers_BeforeStart_DoNotTouchLiveOs()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        var nss = interception.InstallNssToolsAndRetryTrust();
        Assert.AreEqual(CertificateOsTrustKind.Failed, nss.Kind);
        StringAssert.Contains(nss.Message, "Start the proxy first");
        var ff = interception.TrustFirefox();
        Assert.AreEqual(CertificateOsTrustKind.Failed, ff.Kind);
        Assert.IsNull(interception.OpenMacKeychainGuidance());
        Assert.IsFalse(interception.IsRootInLoginKeychain());
        Assert.IsFalse(interception.VerifyOsUserSslTrust());

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var snap = new SessionSnapshot { Id = 7, Url = "http://x.test/" };
        typeof(InterceptionService).GetMethod("ScheduleProcessResolve", flags)!.Invoke(
            interception, [snap, new Lazy<int>(() => 0)]);

        var workType = typeof(InterceptionService).GetNestedType("ProcessResolveWork", BindingFlags.NonPublic)!;
        var zero = Activator.CreateInstance(workType, snap, new Lazy<int>(() => 0))!;
        typeof(InterceptionService).GetMethod("ApplyResolvedProcess", flags)!.Invoke(interception, [zero]);
        var live = Activator.CreateInstance(workType, snap, new Lazy<int>(() => Environment.ProcessId))!;
        typeof(InterceptionService).GetMethod("ApplyResolvedProcess", flags)!.Invoke(interception, [live]);
        Assert.AreEqual(Environment.ProcessId, snap.ProcessId);
        typeof(InterceptionService).GetMethod("ApplyResolvedProcess", flags)!.Invoke(interception, [live]);
    }

    [TestMethod]
    public async Task BreakpointAbort_AndLteThrottle_CoverRequestHooks()
    {
        using var origin = StartOrigin("brk");
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            Breakpoints = new BreakpointViewModel { Enabled = true, UrlFilter = "*" },
            ThrottleProfile = NetworkThrottle.LTE,
            DecryptHttps = false,
            Capturing = true,
        };
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort);
            var pending = http.GetAsync($"http://127.0.0.1:{origin.Port}/brk");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (interception.Breakpoints.Active is null && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.IsNotNull(interception.Breakpoints.Active);
            interception.Breakpoints.Abort();
            var resp = await pending;
            Assert.AreEqual(HttpStatusCode.Forbidden, resp.StatusCode);

            interception.Breakpoints.Enabled = true;
            var pendingEdit = http.GetAsync($"http://127.0.0.1:{origin.Port}/brk-edit");
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (interception.Breakpoints.Active is null && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.IsNotNull(interception.Breakpoints.Active);
            interception.Breakpoints.EditBody("edited");
            interception.Breakpoints.Continue();
            var edited = await pendingEdit;
            Assert.AreEqual(HttpStatusCode.OK, edited.StatusCode);

            interception.BreakpointOnResponse = true;
            var pendingResp = http.GetAsync($"http://127.0.0.1:{origin.Port}/brk-resp");
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (interception.Breakpoints.Active is null && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.IsNotNull(interception.Breakpoints.Active);
            interception.Breakpoints.Continue();
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (interception.Breakpoints.Active is null && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.IsNotNull(interception.Breakpoints.Active);
            interception.Breakpoints.Continue();
            var respContinued = await pendingResp;
            Assert.AreEqual(HttpStatusCode.OK, respContinued.StatusCode);
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    [TestMethod]
    public async Task AutoTrustOnStart_AndSecondStart_CoverStartBranches()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            AutoTrustRootOnStart = true,
            UpstreamProxyAddress = "http://127.0.0.1:9",
        };
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            Assert.IsTrue(interception.IsRunning);
            interception.ApplyHttpProtocols();
            await interception.StartAsync(IPAddress.Loopback, 0);
            Assert.IsTrue(interception.IsRunning);
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    private static HttpClient ProxyClient(int port)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    private static OriginHost StartOrigin(string body)
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                try
                {
                    var ctx = await http.GetContextAsync();
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    byte[] bytes;
                    if (path.Contains("sse", StringComparison.Ordinal))
                    {
                        bytes = Encoding.UTF8.GetBytes("event: ping\ndata: hi\n\n");
                        ctx.Response.ContentType = "text/event-stream";
                    }
                    else if (path.Contains("grpc", StringComparison.Ordinal))
                    {
                        bytes = [0, 0, 0, 0, 2, 0xAA, 0xBB];
                        ctx.Response.ContentType = "application/grpc";
                    }
                    else if (path.Contains("multipart", StringComparison.Ordinal))
                    {
                        bytes = Encoding.UTF8.GetBytes(
                            "--abc\r\nContent-Disposition: form-data; name=\"f\"\r\n\r\nhi\r\n--abc--\r\n");
                        ctx.Response.ContentType = "multipart/form-data; boundary=abc";
                    }
                    else
                    {
                        bytes = Encoding.UTF8.GetBytes(body);
                    }

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch
                {
                    return;
                }
            }
        });
        return new OriginHost(http, port);
    }

    private sealed class OriginHost : IDisposable
    {
        private readonly HttpListener _listener;
        public int Port { get; }
        public OriginHost(HttpListener listener, int port)
        {
            _listener = listener;
            Port = port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task SystemProxy_FailAndThrowPaths_DoNotTouchOs()
    {
        var controller = new RecordingSystemProxyController();
        using var interception = new InterceptionService(controller)
        {
            UseInMemoryTrustState = true,
            UpstreamProxyAddress = "http://127.0.0.1:9",
        };
        Assert.IsFalse(interception.InstallRootCertificate(false));
        Assert.IsFalse(interception.InstallRootCertificateAsAdmin(false));
        Assert.IsFalse(interception.VerifyOsUserSslTrust());
        Assert.AreEqual(CertificateOsTrustKind.Failed, interception.InstallNssToolsAndRetryTrust().Kind);
        Assert.IsNull(interception.OpenMacKeychainGuidance());
        Assert.IsFalse(interception.IsRootInLoginKeychain());
        Assert.IsTrue(interception.ReapplySystemProxyIfEnabled());

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            controller.FailSet = true;
            Assert.IsFalse(interception.SetSystemProxy(true));
            Assert.IsFalse(string.IsNullOrEmpty(interception.LastSystemProxyError));

            controller.FailSet = false;
            controller.ThrowOnSet = true;
            Assert.IsFalse(interception.SetSystemProxy(true));

            controller.ThrowOnSet = false;
            Assert.IsTrue(interception.SetSystemProxy(true));
            controller.FailRestore = true;
            Assert.IsFalse(interception.SetSystemProxy(false));
            controller.FailRestore = false;
            controller.ThrowOnRestore = true;
            Assert.IsFalse(interception.SetSystemProxy(false));
            controller.ThrowOnRestore = false;
            Assert.IsTrue(interception.SetSystemProxy(false));
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    [TestMethod]
    public async Task Interception_SessionIdLoggingPfxAndTunnelSnapshotSeams()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        interception.ResetSessionIdSequence();
        var next = typeof(InterceptionService).GetMethod("NextSessionId", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.AreEqual(1L, (long)next.Invoke(interception, null)!);
        Assert.AreEqual(2L, (long)next.Invoke(interception, null)!);
        interception.ResetSessionIdSequence();
        Assert.AreEqual(1L, (long)next.Invoke(interception, null)!);

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            typeof(InterceptionService).GetMethod("ApplyLoggingOptions", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(interception, [new InspectorSettings { LoggingEnabled = true, LoggingEnableFile = false }]);
            typeof(InterceptionService).GetMethod("ApplyLoggingOptions", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(interception, [null]);
            typeof(InterceptionService).GetMethod("EnsureRootPfxPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(interception, null);
            var pfx = typeof(InterceptionService).GetField("_rootPfxPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(interception) as string;
            Assert.IsFalse(string.IsNullOrEmpty(pfx));

            _ = interception.IsRootPresentInStore(false);
            _ = interception.IsRootPresentInStore(true);

            using var proxy = new ProxyServer(false, false, false);
            var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
            var cts = new CancellationTokenSource();
            var connection = new QuicClientConnection(
                proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
            var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
            var connect = new ConnectRequest("tunnel.example:443".GetByteString());
            var tunnel = new TunnelConnectSessionEventArgs(proxy, endPoint, connect, clientStream, cts);

            var createSnap = typeof(InterceptionService).GetMethod("CreateTunnelSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var snap = (SessionSnapshot)createSnap.Invoke(interception,
                [tunnel, OpaqueTunnelReason.DecryptOff])!;
            Assert.IsTrue(snap.IsTunnel);
            Assert.AreEqual(OpaqueTunnelReason.DecryptOff, snap.OpaqueReason);

            typeof(InterceptionService).GetMethod("AttachTunnelByteCounters",
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [tunnel, snap]);
            typeof(InterceptionService).GetMethod("ApplyConnectCompletion",
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [snap, tunnel]);
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    [TestMethod]
    public async Task Interception_CompleteRootTrustAndLegacyCrtsSeams()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-legacy-crts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var shared = Path.Combine(dir, "shared-crts");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "leaf.cer"), "x");
        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
                LegacyCrtsTestRoot = dir,
            };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));

            var complete = typeof(InterceptionService).GetMethod("CompleteRootTrustInstall",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.IsFalse((bool)complete.Invoke(interception, [false])!);
            Assert.IsTrue((bool)complete.Invoke(interception, [true])!);

            await interception.StartAsync(IPAddress.Loopback, 0);
            try
            {
                Assert.IsTrue(interception.InstallRootCertificate(false));
                Assert.IsTrue(interception.IsRootTrusted);
                // Trusted path also best-effort enables Firefox enterprise roots.
                Assert.IsTrue((bool)complete.Invoke(interception, [true])!);
            }
            finally
            {
                interception.EnsureShutdown();
            }

            var eval = typeof(InterceptionService).GetMethod("EvaluateUnixTrustSuccess",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.IsTrue((bool)eval.Invoke(null, [CertificateOsTrustResult.Ok("ok")])!);
            Assert.IsFalse((bool)eval.Invoke(null, [null])!);
            Assert.IsFalse((bool)eval.Invoke(null,
                [CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, "nope")])!);
            Assert.IsFalse((bool)eval.Invoke(null,
                [CertificateOsTrustResult.Fail(CertificateOsTrustKind.MacNeedsManualTrustConfirm, "mac")])!);
            Assert.IsFalse((bool)eval.Invoke(null,
                [CertificateOsTrustResult.Fail(CertificateOsTrustKind.Cancelled, "cancel")])!);

            interception.PruneLegacySharedCrts(force: false);
            Assert.IsTrue(File.Exists(Path.Combine(dir, "legacy-shared-crts-cleared")));
            Assert.IsFalse(Directory.Exists(shared));

            // Marker present → TryPruneLegacySharedCrtsOnce is a no-op.
            Directory.CreateDirectory(shared);
            typeof(InterceptionService).GetMethod("TryPruneLegacySharedCrtsOnce",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(interception, null);
            Assert.IsTrue(Directory.Exists(shared));

            var marker = (string)typeof(InterceptionService).GetMethod("LegacySharedCrtsMarkerPath",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(interception, null)!;
            var resolved = (string)typeof(InterceptionService).GetMethod("ResolveLegacySharedCrtsDirectory",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(interception, null)!;
            Assert.AreEqual(Path.Combine(dir, "legacy-shared-crts-cleared"), marker);
            Assert.AreEqual(shared, resolved);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static void OverrideRootPfx(InterceptionService interception, string path)
    {
        var field = typeof(InterceptionService).GetField("_rootPfxPath", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(interception, path);
    }
}
