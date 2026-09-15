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
        var bodyHint = typeof(MainWindowViewModel).GetMethod("BuildBodyCaptureHint", PrivateStatic)!;
        var hexHint = typeof(MainWindowViewModel).GetMethod("BuildHexCaptureHint", PrivateStatic)!;
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
        Assert.AreEqual("", (string)bodyHint.Invoke(null, [none])!);
        Assert.AreEqual("", (string)hexHint.Invoke(null, [none])!);

        var respOnly = new SessionSnapshot
        {
            ResponseBodyCapture = BodyCaptureState.Truncated,
            ResponseBodyOriginalSize = 9000,
            ResponseBodyBytes = [1, 2, 3],
            ResponseBodyStreamOpen = false,
        };
        StringAssert.StartsWith((string)bodyHint.Invoke(null, [respOnly])!, "Response:");
        Assert.IsFalse(string.IsNullOrEmpty((string)hexHint.Invoke(null, [respOnly])!));

        var reqOnly = new SessionSnapshot
        {
            RequestBodyCapture = BodyCaptureState.NotCaptured,
            RequestBodyOriginalSize = InspectorBodyLimits.MaxMapLocalFileBytes,
            RequestBodyBytes = [9],
        };
        StringAssert.StartsWith((string)bodyHint.Invoke(null, [reqOnly])!, "Request:");

        var both = new SessionSnapshot
        {
            RequestBodyCapture = BodyCaptureState.Streaming,
            ResponseBodyCapture = BodyCaptureState.Streaming,
            ResponseBodyStreamOpen = true,
            RequestBodyBytes = [1],
            ResponseBodyBytes = [2, 3],
            BodySize = 10,
        };
        StringAssert.Contains((string)bodyHint.Invoke(null, [both])!, "Request:");
        StringAssert.Contains((string)bodyHint.Invoke(null, [both])!, "Response:");

        var image = new SessionSnapshot
        {
            ContentType = "image/png",
            RequestBodyBytes = [0x89, 0x50],
            ResponseBodyBytes = [0x89, 0x50],
            ResponseHeadersText = "Content-Type: image/png\r\n",
        };
        StringAssert.Contains((string)core.Invoke(null, [image, true])!, "image");

        var json = new SessionSnapshot
        {
            ContentType = "application/json",
            RequestHeadersText = "Content-Type: application/json\r\n",
            ResponseHeadersText = "Content-Type: application/json\r\n",
            RequestBodyText = "{\"a\":1}",
            ResponseBodyText = "{\"b\":2}",
        };
        StringAssert.Contains((string)core.Invoke(null, [json, true])!, "Request");
        StringAssert.Contains((string)core.Invoke(null, [json, false])!, "{");

        var truncatedBad = new SessionSnapshot
        {
            ContentType = "application/json",
            ResponseHeadersText = "Content-Type: application/json\r\n",
            ResponseBodyText = "{not-json",
            ResponseBodyCapture = BodyCaptureState.Truncated,
        };
        Assert.IsFalse(string.IsNullOrEmpty((string)core.Invoke(null, [truncatedBad, true])!));
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

        var plain = (string)prefix.Invoke(null, [new SessionSnapshot(), "body"])!;
        Assert.AreEqual("body", plain);
        var transcoded = new SessionSnapshot
        {
            IsTranscoded = true,
            RequestBodyText = "{}",
            ResponseBodyText = "{}",
            UpstreamRequestBodyBytes = [1],
            GrpcFrames = [new GrpcFrameSnapshot { Compressed = true, Length = 1, HexPreview = "aa" }],
        };
        StringAssert.Contains((string)prefix.Invoke(null, [transcoded, "body"])!, "Client");
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
            vm.SelectedInspectTabIndex = 1;
            vm.BodyPrettyMode = true;
            Assert.IsTrue(vm.CanSaveRequestBody);
            Assert.IsTrue(vm.CanSaveResponseBody);
            Assert.IsTrue(vm.ShowBodyCaptureHint || !string.IsNullOrEmpty(vm.SelectedBody));

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
            Assert.IsNull(vm.BodyPreviewBitmap);
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
