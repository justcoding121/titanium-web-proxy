using System.Net;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Tests;

[TestClass]
public class BodyInspectCoverageTests
{
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void BuildBodyAndHexCaptureHints_CoverAllBannerBranches()
    {
        var bodyHint = typeof(MainWindowViewModel).GetMethod("BuildSideCaptureHint", PrivateStatic)!;
        var looksImage = typeof(MainWindowViewModel).GetMethod("LooksLikeImageHeaders", PrivateStatic)!;
        var core = typeof(MainWindowViewModel).GetMethod("BuildSelectedBodyTextCore", PrivateStatic)!;

        Assert.IsFalse((bool)looksImage.Invoke(null, [null])!);
        Assert.IsFalse((bool)looksImage.Invoke(null, ["Accept: */*\r\n"])!);
        Assert.IsTrue((bool)looksImage.Invoke(null, ["Content-Type: image/png\r\n"])!);

        var none = new SessionSnapshot
        {
            RequestBodyCapture = BodyCaptureState.None,
            ResponseBodyCapture = BodyCaptureState.None,
        };
        Assert.AreEqual("", (string)bodyHint.Invoke(null, [none, true, false])!);
        Assert.AreEqual("", (string)bodyHint.Invoke(null, [none, false, true])!);

        var respOnly = new SessionSnapshot
        {
            ResponseBodyCapture = BodyCaptureState.Truncated,
            ResponseBodyOriginalSize = 9000,
            ResponseBodyBytes = [1, 2, 3],
            ResponseBodyStreamOpen = false,
        };
        StringAssert.Contains((string)bodyHint.Invoke(null, [respOnly, false, false])!, "Showing first");
        Assert.IsFalse(string.IsNullOrEmpty((string)bodyHint.Invoke(null, [respOnly, false, true])!));

        var reqOnly = new SessionSnapshot
        {
            RequestBodyCapture = BodyCaptureState.NotCaptured,
            RequestBodyOriginalSize = InspectorBodyLimits.MaxMapLocalFileBytes,
            RequestBodyBytes = [9],
        };
        StringAssert.Contains((string)bodyHint.Invoke(null, [reqOnly, true, false])!, "not captured");

        var both = new SessionSnapshot
        {
            RequestBodyCapture = BodyCaptureState.Streaming,
            ResponseBodyCapture = BodyCaptureState.Streaming,
            ResponseBodyStreamOpen = true,
            RequestBodyBytes = [1],
            ResponseBodyBytes = [2, 3],
            BodySize = 10,
        };
        StringAssert.Contains((string)bodyHint.Invoke(null, [both, true, false])!, "Streaming");
        StringAssert.Contains((string)bodyHint.Invoke(null, [both, false, false])!, "Streaming");

        var image = new SessionSnapshot
        {
            ContentType = "image/png",
            RequestBodyBytes = [0x89, 0x50],
            ResponseBodyBytes = [0x89, 0x50],
            RequestHeadersText = "Content-Type: image/png\r\n",
            ResponseHeadersText = "Content-Type: image/png\r\n",
        };
        StringAssert.Contains((string)core.Invoke(null, [image, false, true, false])!, "image");
        StringAssert.Contains((string)core.Invoke(null, [image, true, false, true])!, "89 50");

        var json = new SessionSnapshot
        {
            ContentType = "application/json",
            RequestHeadersText = "Content-Type: application/json\r\n",
            ResponseHeadersText = "Content-Type: application/json\r\n",
            RequestBodyText = "{\"a\":1}",
            ResponseBodyText = "{\"b\":2}",
        };
        var prettyReq = (string)core.Invoke(null, [json, true, true, false])!;
        StringAssert.Contains(prettyReq, "\"a\"");
        Assert.IsFalse(prettyReq.Contains("\"b\"", StringComparison.Ordinal));
        StringAssert.Contains((string)core.Invoke(null, [json, false, false, false])!, "{");

        var truncatedBad = new SessionSnapshot
        {
            ContentType = "application/json",
            ResponseHeadersText = "Content-Type: application/json\r\n",
            ResponseBodyText = "{not-json",
            ResponseBodyCapture = BodyCaptureState.Truncated,
        };
        Assert.IsFalse(string.IsNullOrEmpty((string)core.Invoke(null, [truncatedBad, false, true, false])!));
    }

    [TestMethod]
    public void BodyTabs_HexDisablesPretty_AndKeepsEachSideSeparate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-body-sides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            settings.Current.AutoStartCapture = false;
            settings.Save();
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                new ScriptedInspectorDialogs())
            {
                BindPort = 0,
                BindAddress = "127.0.0.1",
            };

            var snap = new SessionSnapshot
            {
                Id = 9,
                Method = "POST",
                StatusCode = 200,
                Url = "https://sides.test/q?x=1",
                RequestHeadersText = "Content-Type: application/json\r\nCookie: a=1\r\n",
                ResponseHeadersText = "Content-Type: application/json\r\n",
                RequestBodyText = "{\"req\":1}",
                ResponseBodyText = "{\"resp\":2}",
                RequestBodyBytes = Encoding.UTF8.GetBytes("{\"req\":1}"),
                ResponseBodyBytes = Encoding.UTF8.GetBytes("{\"resp\":2}"),
                RequestBodyCapture = BodyCaptureState.Complete,
                ResponseBodyCapture = BodyCaptureState.Truncated,
                ResponseBodyOriginalSize = 9000,
            };
            vm.SeedSession(snap);
            vm.SelectedSession = snap;

            Assert.IsTrue(vm.BodyPrettyMode);
            Assert.IsFalse(vm.BodyHexMode);
            Assert.IsTrue(vm.BodyPrettyEnabled);
            Assert.IsTrue(vm.CanSaveRequestBody);
            Assert.IsTrue(vm.CanSaveResponseBody);
            Assert.IsFalse(vm.ShowRequestBodyCaptureHint);
            Assert.IsTrue(vm.ShowResponseBodyCaptureHint);
            Assert.IsFalse(vm.ResponseBodyCaptureHint.Contains("Request", StringComparison.Ordinal));

            StringAssert.Contains(vm.SelectedRequestHeaders, "Cookie");
            StringAssert.Contains(vm.SelectedRequestHeaders, "=== Query ===");
            Assert.IsFalse(vm.SelectedRequestHeaders.Contains("=== Response ===", StringComparison.Ordinal));
            StringAssert.Contains(vm.SelectedResponseHeaders, "application/json");

            vm.SelectedInspectTabIndex = (int)InspectTab.RequestBody;
            StringAssert.Contains(vm.SelectedRequestBody, "req");
            Assert.IsFalse(vm.SelectedRequestBody.Contains("resp", StringComparison.Ordinal));

            vm.SelectedInspectTabIndex = (int)InspectTab.ResponseBody;
            StringAssert.Contains(vm.SelectedResponseBody, "resp");
            Assert.IsFalse(vm.SelectedResponseBody.Contains("\"req\"", StringComparison.Ordinal));

            vm.BodyHexMode = true;
            Assert.IsTrue(vm.BodyPrettyMode);
            Assert.IsFalse(vm.BodyPrettyEnabled);
            Assert.AreEqual("Not applicable in hex view", vm.BodyPrettyToolTip);
            StringAssert.Contains(vm.SelectedResponseBody, "72 65 73 70");
            Assert.IsFalse(vm.SelectedResponseBody.Contains("72 65 71", StringComparison.Ordinal));

            vm.BodyHexMode = false;
            Assert.IsTrue(vm.BodyPrettyEnabled);
            Assert.IsTrue(vm.BodyPrettyMode);
            StringAssert.Contains(vm.SelectedResponseBody, "resp");

            var requestOnly = new SessionSnapshot
            {
                Id = 10,
                Method = "POST",
                Url = "https://sides.test/only",
                RequestBodyText = "ping",
                RequestBodyBytes = Encoding.UTF8.GetBytes("ping"),
                RequestBodyCapture = BodyCaptureState.Complete,
            };
            vm.SeedSession(requestOnly);
            vm.SelectedSession = requestOnly;
            vm.SelectedInspectTabIndex = (int)InspectTab.RequestBody;
            StringAssert.Contains(vm.SelectedRequestBody, "ping");
            Assert.AreEqual("Waiting for response…", vm.SelectedResponseHeaders);
            vm.SelectedInspectTabIndex = (int)InspectTab.ResponseBody;
            Assert.AreEqual("Waiting for response…", vm.SelectedResponseBody);

            // Once the session ended with no response, say so (and why) instead of "waiting".
            requestOnly.DurationMs = 30;
            requestOnly.FailureReason = "Connection reset by the server.";
            vm.SelectedSession = null;
            vm.SelectedSession = requestOnly;
            vm.SelectedInspectTabIndex = (int)InspectTab.ResponseBody;
            Assert.AreEqual("No response received — Connection reset by the server.", vm.SelectedResponseBody);
            Assert.AreEqual(0.65, vm.ResponseBodyOpacity);
            Assert.IsTrue(vm.CanSaveRequestBody);
            Assert.IsFalse(vm.CanSaveResponseBody);
            Assert.IsTrue(vm.ShowBodyModeToggles);

            // Hex still works when only decoded text was kept (no raw bytes).
            var textOnly = new SessionSnapshot
            {
                Id = 11,
                Method = "GET",
                StatusCode = 200,
                Url = "https://sides.test/text",
                ResponseBodyText = "abc",
            };
            vm.SeedSession(textOnly);
            vm.SelectedSession = textOnly;
            vm.SelectedInspectTabIndex = (int)InspectTab.ResponseBody;
            vm.BodyHexMode = true;
            StringAssert.Contains(vm.SelectedResponseBody, "61 62 63");
            vm.BodyHexMode = false;

            // Pretty/Hex are meaningless for CONNECT tunnels.
            var tunnel = new SessionSnapshot { Id = 12, Method = "CONNECT", Url = "host:443", IsTunnel = true };
            vm.SeedSession(tunnel);
            vm.SelectedSession = tunnel;
            Assert.IsFalse(vm.ShowBodyModeToggles);
            Assert.IsFalse(vm.ShowRequestBodyCaptureHint);
            Assert.IsFalse(vm.ShowResponseBodyCaptureHint);
            vm.SelectedInspectTabIndex = (int)InspectTab.ResponseBody;
            StringAssert.Contains(vm.SelectedResponseBody, "CONNECT tunnel");

            // A vanished contextual tab returns to the last core tab, not always tab 0.
            vm.SelectedSession = snap;
            vm.SelectedInspectTabIndex = (int)InspectTab.ResponseHeaders;
            vm.SelectedInspectTabIndex = (int)InspectTab.Sse;
            vm.SelectedSession = requestOnly;
            Assert.AreEqual((int)InspectTab.ResponseHeaders, vm.SelectedInspectTabIndex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [TestMethod]
    public void DescribeNoResponse_NamesTheFailure()
    {
        var describe = typeof(InterceptionService).GetMethod(
            "DescribeNoResponse", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Run(Exception? ex) => (string)describe.Invoke(null, [ex])!;

        StringAssert.Contains(Run(null), "closed before the server replied");
        StringAssert.Contains(
            Run(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)),
            "refused");
        StringAssert.Contains(
            Run(new IOException("x", new System.Net.Sockets.SocketException(
                (int)System.Net.Sockets.SocketError.ConnectionReset))),
            "reset");
        StringAssert.Contains(Run(new TimeoutException()), "Timed out");
        StringAssert.Contains(Run(new OperationCanceledException()), "disconnected");
        StringAssert.Contains(Run(new InvalidOperationException("boom")), "boom");
    }

    [TestMethod]
    public void BuildSelectedInspectTabs_CoverFramesSseAndProtobuf()
    {
        var frames = typeof(MainWindowViewModel).GetMethod("BuildSelectedFramesText", PrivateStatic)!;
        var sse = typeof(MainWindowViewModel).GetMethod("BuildSelectedSseText", PrivateStatic)!;
        var proto = typeof(MainWindowViewModel).GetMethod("BuildSelectedProtobufText", PrivateStatic)!;
        var prefix = typeof(MainWindowViewModel).GetMethod("AppendTranscodePrefix", PrivateStatic)!;

        Assert.AreEqual("", (string)frames.Invoke(null, [new SessionSnapshot()])!);
        Assert.AreEqual("(no frames parsed)",
            (string)frames.Invoke(null, [new SessionSnapshot { IsWebSocket = true }])!);
        var withFrames = new SessionSnapshot
        {
            WebSocketFrames =
            [
                new WebSocketFrameSnapshot
                {
                    Direction = "Client", Opcode = "Text", PayloadPreview = "hi",
                },
            ],
        };
        StringAssert.Contains((string)frames.Invoke(null, [withFrames])!, "Text");

        Assert.AreEqual("", (string)sse.Invoke(null, [new SessionSnapshot()])!);
        Assert.AreEqual("(no events parsed)",
            (string)sse.Invoke(null, [new SessionSnapshot { IsServerSentEvents = true }])!);
        var withSse = new SessionSnapshot
        {
            SseEvents = [new SseEventSnapshot { Event = "msg", Id = "1", Data = "x" }],
        };
        StringAssert.Contains((string)sse.Invoke(null, [withSse])!, "msg");

        Assert.AreEqual("decoded",
            (string)proto.Invoke(null, [new SessionSnapshot { ProtobufDecodedText = "decoded" }])!);
        _ = (string)proto.Invoke(null, [new SessionSnapshot
        {
            IsGrpc = true,
            ResponseBodyBytes = [0, 0, 0, 0, 1, 0x0a],
        }])!;
        Assert.AreEqual("", (string)proto.Invoke(null, [new SessionSnapshot()])!);

        var plain = (string)prefix.Invoke(null, [new SessionSnapshot(), "body", true, false])!;
        Assert.AreEqual("body", plain);
        var transcoded = new SessionSnapshot
        {
            IsTranscoded = true,
            RequestBodyText = "{}",
            ResponseBodyText = "{}",
            UpstreamRequestBodyBytes = [1],
            GrpcFrames = [new GrpcFrameSnapshot { Compressed = true, Length = 1, HexPreview = "aa" }],
        };
        StringAssert.Contains((string)prefix.Invoke(null, [transcoded, "body", true, false])!, "Client");
        Assert.AreEqual("body", (string)prefix.Invoke(null, [transcoded, "body", true, true])!);
    }

    [TestMethod]
    public async Task SaveAndLoadComposerBody_CoverPickerGuardsAndTruncation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-body-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            settings.Current.AutoStartCapture = false;
            settings.Save();
            var savePath = Path.Combine(dir, "body.bin");
            var smallFile = Path.Combine(dir, "small.txt");
            File.WriteAllText(smallFile, "{\"ok\":true}");
            var largeFile = Path.Combine(dir, "large.bin");
            await using (var fs = File.Create(largeFile))
            {
                fs.SetLength(InspectorBodyLimits.MaxBodyBytes + 10);
            }

            var picker = new ScriptedInspectorPathPicker { SavePath = null, OpenPath = null };
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                new ScriptedInspectorDialogs(),
                picker)
            {
                BindPort = 0,
                BindAddress = "127.0.0.1",
            };

            await ExecuteAsync(vm.SaveRequestBodyCommand);
            StringAssert.Contains(vm.StatusText, "Select a session");

            var notCaptured = new SessionSnapshot
            {
                Id = 1,
                Method = "POST",
                Url = "https://body.test/x",
                RequestBodyCapture = BodyCaptureState.NotCaptured,
                RequestBodyOriginalSize = 99,
            };
            vm.SeedSession(notCaptured);
            vm.SelectedSession = notCaptured;
            await ExecuteAsync(vm.SaveRequestBodyCommand);
            StringAssert.Contains(vm.StatusText, "not captured");

            var complete = new SessionSnapshot
            {
                Id = 2,
                Method = "POST",
                Url = "https://body.test/y",
                RequestBodyBytes = Encoding.UTF8.GetBytes("hello-body"),
                RequestBodyCapture = BodyCaptureState.Complete,
                ResponseBodyBytes = Encoding.UTF8.GetBytes("resp"),
                ResponseBodyCapture = BodyCaptureState.Truncated,
                ResponseBodyOriginalSize = 5000,
                ContentType = "application/json",
                ResponseHeadersText = "Content-Type: application/json\r\n",
                ResponseBodyText = "{bad",
            };
            vm.SeedSession(complete);
            vm.SelectedSession = complete;
            vm.SelectedInspectTabIndex = (int)InspectTab.ResponseBody;
            vm.BodyPrettyMode = true;
            Assert.IsTrue(vm.CanSaveRequestBody);
            Assert.IsTrue(vm.CanSaveResponseBody);
            Assert.IsTrue(vm.ShowResponseBodyCaptureHint || !string.IsNullOrEmpty(vm.SelectedResponseBody));

            picker.SavePath = savePath;
            await ExecuteAsync(vm.SaveRequestBodyCommand);
            Assert.IsTrue(File.Exists(savePath));
            await ExecuteAsync(vm.SaveResponseBodyCommand);
            StringAssert.Contains(vm.StatusText, "incomplete");

            picker.SavePath = null;
            await ExecuteAsync(vm.SaveRequestBodyCommand);

            picker.OpenPath = null;
            await ExecuteAsync(vm.LoadComposerBodyFileCommand);

            picker.OpenPath = Path.Combine(dir, "missing.txt");
            await ExecuteAsync(vm.LoadComposerBodyFileCommand);
            StringAssert.Contains(vm.StatusText, "not found");

            picker.OpenPath = smallFile;
            await ExecuteAsync(vm.LoadComposerBodyFileCommand);
            Assert.AreEqual("{\"ok\":true}", vm.ComposerBody);

            picker.OpenPath = largeFile;
            await ExecuteAsync(vm.LoadComposerBodyFileCommand);
            Assert.AreEqual(largeFile, vm.ComposerBodyFilePath);
            Assert.IsTrue(vm.HasComposerBodyFile);

            // Invalid image bytes exercise UpdateBodyPreviewImage catch.
            var bogusImage = new SessionSnapshot
            {
                Id = 3,
                Method = "GET",
                Url = "https://body.test/img",
                ContentType = "image/png",
                ResponseHeadersText = "Content-Type: image/png\r\n",
                ResponseBodyBytes = [1, 2, 3, 4],
                ResponseBodyCapture = BodyCaptureState.Complete,
            };
            vm.SeedSession(bogusImage);
            vm.SelectedSession = bogusImage;
            Assert.IsNull(vm.ResponseBodyPreviewBitmap);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [TestMethod]
    public void FormatRemoveRootPromptStatus_CoversOsBranches()
    {
        var format = typeof(MainWindowViewModel).GetMethod("FormatRemoveRootPromptStatus", PrivateStatic)!;
        var one = (string)format.Invoke(null, [1, 1])!;
        var many = (string)format.Invoke(null, [3, 2])!;
        Assert.IsFalse(string.IsNullOrWhiteSpace(one));
        Assert.IsFalse(string.IsNullOrWhiteSpace(many));
        Assert.AreNotEqual(one, many);

        var running = typeof(MainWindowViewModel).GetMethod("IsFirefoxRunningTrustError", PrivateStatic)!;
        Assert.IsTrue((bool)running.Invoke(null,
        [
            CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, "Quit Firefox and retry"),
        ])!);
        Assert.IsTrue((bool)running.Invoke(null,
        [
            CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, "Firefox is running"),
        ])!);
        Assert.IsFalse((bool)running.Invoke(null,
        [
            CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, "other"),
        ])!);
    }

    [TestMethod]
    public async Task ReverifyDecryptAndAwaitTrustBg_CoverTrustPartialClass()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-trust-bg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            settings.Current.AutoStartCapture = false;
            settings.Save();
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                new ScriptedInspectorDialogs())
            {
                BindPort = 0,
                BindAddress = "127.0.0.1",
            };

            await ExecuteAsync(vm.StartCaptureCommand);
            await Task.Delay(200);
            Assert.IsTrue(interception.InstallRootCertificate(false));
            vm.DecryptHttps = true;
            Assert.IsTrue(vm.DecryptHttps);

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            await (Task)typeof(MainWindowViewModel)
                .GetMethod("ReverifyDecryptTrustInBackgroundAsync", flags)!.Invoke(vm, null)!;

            interception.UntrustRootCertificate(false);
            await (Task)typeof(MainWindowViewModel)
                .GetMethod("ReverifyDecryptTrustInBackgroundAsync", flags)!.Invoke(vm, null)!;
            Assert.IsFalse(vm.DecryptHttps);

            interception.ScheduleClearPendingFirefoxRootTrust();
            await (Task)typeof(MainWindowViewModel)
                .GetMethod("AwaitPriorFirefoxTrustBackgroundAsync", flags)!.Invoke(vm, null)!;

            Assert.IsTrue(await (Task<bool>)typeof(MainWindowViewModel)
                .GetMethod("TryCompleteMacSslTrustForDecryptAsync", flags)!.Invoke(vm, null)!);

            // Double-stop hits _stopBusy / not-running early returns.
            await ExecuteAsync(vm.StopCaptureCommand);
            await ExecuteAsync(vm.StopCaptureCommand);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [TestMethod]
    public void PaneNavAndDecryptHealth_CoverRemainingPropertyBranches()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-pane-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            settings.Current.AutoStartCapture = false;
            settings.Save();
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                new ScriptedInspectorDialogs())
            {
                BindPort = 0,
                BindAddress = "127.0.0.1",
            };

            Assert.AreEqual("", vm.DecryptTrustHealthText);
            Assert.IsFalse(vm.ShowDecryptTrustHealth);
            Assert.IsFalse(vm.IsDecryptTrustHealthy);

            typeof(MainWindowViewModel).GetMethod("SetDecryptHttpsCore",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [true]);
            Assert.AreEqual("CA not trusted", vm.DecryptTrustHealthText);
            Assert.IsTrue(vm.ShowDecryptTrustHealth);
            Assert.IsFalse(vm.IsDecryptTrustHealthy);

            interception.StartAsync(IPAddress.Loopback, 0).GetAwaiter().GetResult();
            try
            {
                Assert.IsTrue(interception.InstallRootCertificate(false));
                Assert.AreEqual("CA trusted", vm.DecryptTrustHealthText);
                Assert.IsTrue(vm.IsDecryptTrustHealthy);
            }
            finally
            {
                interception.EnsureShutdown();
            }

            vm.ShowSessionDetails = true;
            foreach (var idx in new[] { 0, 1, 2, 3, 4, 5, 99 })
            {
                vm.SelectedPaneNavIndex = idx > 5 ? 0 : idx;
                _ = vm.PaneContentTitle;
                _ = vm.SessionDetailsPaneWidth;
                _ = vm.SessionDetailsPaneMinWidth;
                _ = vm.IsInspectRailPressed;
                _ = vm.IsComposerRailPressed;
                _ = vm.IsBreakpointsRailPressed;
                _ = vm.IsAutoResponderRailPressed;
                _ = vm.IsScriptsRailPressed;
                _ = vm.IsMapRemoteRailPressed;
                _ = vm.ShowScriptsPane;
                _ = vm.ShowMapRemotePane;
            }

            vm.SelectedPaneNavIndex = 0;
            vm.SelectedToolsTabIndex = 2; // jumps off Inspect
            Assert.AreEqual(3, vm.SelectedPaneNavIndex);
            vm.SelectedOuterPaneIndex = 0;
            Assert.AreEqual(0, vm.SelectedPaneNavIndex);
            vm.SelectedOuterPaneIndex = 1;
            Assert.IsTrue(vm.SelectedPaneNavIndex >= 1);
            vm.SelectedOuterPaneIndex = 1; // already on tools → SelectedOuterPaneIndex arm

            var streaming = new SessionSnapshot
            {
                Id = 42,
                Method = "GET",
                Url = "https://stream.test/sse",
                ResponseBodyCapture = BodyCaptureState.Streaming,
                ResponseBodyStreamOpen = true,
                IsServerSentEvents = true,
            };
            vm.SeedSession(streaming);
            vm.SelectedSession = streaming;
            vm.ApplyEditBodyCommand.Execute(null);
            StringAssert.Contains(vm.StatusText, "streaming");
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task ExecuteAsync(ICommand command)
    {
        command.Execute(null);
        await Task.Delay(120);
    }
}
