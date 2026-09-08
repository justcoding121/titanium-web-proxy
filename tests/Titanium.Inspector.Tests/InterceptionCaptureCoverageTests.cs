using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network;

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
}
