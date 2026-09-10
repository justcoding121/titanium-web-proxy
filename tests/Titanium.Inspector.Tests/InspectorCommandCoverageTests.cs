using System.Net;
using System.Reflection;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InspectorCommandCoverageTests
{
    [TestMethod]
    public async Task RemainingCommands_CopyDiffComposerAutoResponderMapRemoteAndToggles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-cmd-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();
            var dialogs = new ScriptedInspectorDialogs
            {
                ResetSettingsResult = false,
            };
            var picker = new ScriptedInspectorPathPicker { OpenPath = null, SavePath = null };
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
                dialogs,
                picker)
            {
                BindPort = 0,
                BindAddress = "127.0.0.1",
            };

            Assert.IsFalse(vm.CanCopyAsCurl);
            await ExecuteAsync(vm.CopyAsCurlCommand);
            StringAssert.Contains(vm.StatusText, "Select one session");
            await ExecuteAsync(vm.CopyAsFetchCommand);
            StringAssert.Contains(vm.StatusText, "Select one session");
            await ExecuteAsync(vm.DiffSessionsCommand);
            StringAssert.Contains(vm.StatusText, "exactly two");

            var a = new SessionSnapshot
            {
                Id = 1, Method = "GET", Url = "https://a.test/x", Host = "a.test",
                RequestHeadersText = "Accept: */*\r\n", RequestBodyText = "",
            };
            var b = new SessionSnapshot
            {
                Id = 2, Method = "POST", Url = "https://b.test/y", Host = "b.test",
                RequestHeadersText = "Content-Type: text/plain\r\n", RequestBodyText = "n",
            };
            vm.SeedSession(a);
            vm.SeedSession(b);
            vm.SelectedSession = a;
            vm.SetSelectedSessions([a]);
            await ExecuteAsync(vm.CopyAsCurlCommand);
            StringAssert.Contains(vm.StatusText, "curl");
            await ExecuteAsync(vm.CopyAsFetchCommand);
            StringAssert.Contains(vm.StatusText, "fetch");
            await ExecuteAsync(vm.LoadFromSelectedCommand);
            StringAssert.Contains(vm.StatusText, "Composer");

            vm.SetSelectedSessions([a, b]);
            await ExecuteAsync(vm.DiffSessionsCommand);
            Assert.IsTrue(vm.ShowSessionDetails);
            StringAssert.Contains(vm.StatusText, "Diff");

            vm.ComposerMethod = "GET";
            vm.ComposerUrl = "";
            await ExecuteAsync(vm.SendComposerCommand);
            StringAssert.Contains(vm.StatusText, "Composer URL");

            await ExecuteAsync(vm.AddAutoResponderRuleCommand);
            Assert.IsTrue(vm.AutoResponder.Rules.Count >= 1);
            vm.AutoResponder.SelectedRule = vm.AutoResponder.Rules[0];
            await ExecuteAsync(vm.UpdateAutoResponderRuleCommand);
            await ExecuteAsync(vm.BrowseAutoResponderLocalFileCommand);
            await ExecuteAsync(vm.DeleteAutoResponderRuleCommand);

            await ExecuteAsync(vm.AddMapRemoteRuleCommand);
            Assert.IsTrue(vm.MapRemote.Rules.Count >= 1);
            vm.MapRemote.SelectedRule = vm.MapRemote.Rules[0];
            await ExecuteAsync(vm.UpdateMapRemoteRuleCommand);
            await ExecuteAsync(vm.DeleteMapRemoteRuleCommand);

            await ExecuteAsync(vm.ContinueBreakpointCommand);
            await ExecuteAsync(vm.AbortBreakpointCommand);
            await ExecuteAsync(vm.ApplyEditBodyCommand);
            await ExecuteAsync(vm.CloseSessionDetailsCommand);
            await ExecuteAsync(vm.OpenToolsMapRemoteCommand);
            await ExecuteAsync(vm.ToggleInterceptCommand);
            await ExecuteAsync(vm.ToggleAutoStartCaptureCommand);
            await ExecuteAsync(vm.ToggleAutoSystemProxyOnStartCommand);
            await ExecuteAsync(vm.ToggleIgnoreServerCertificateErrorsCommand);
            await ExecuteAsync(vm.ToggleAddViaHeaderCommand);
            await ExecuteAsync(vm.ToggleDebugLoggingCommand);
            await ExecuteAsync(vm.ToggleCheckForUpdatesOnStartupCommand);
            await ExecuteAsync(vm.SetThemeLightCommand);
            await ExecuteAsync(vm.SetThemeDarkCommand);
            await ExecuteAsync(vm.SetThemeAutomaticCommand);
            await ExecuteAsync(vm.SetUpdateChannelBetaCommand);
            await ExecuteAsync(vm.SetUpdateChannelStableCommand);
            await ExecuteAsync(vm.ResetSettingsCommand);
            StringAssert.Contains(vm.StatusText, "cancelled");

            vm.SelectedSession = a;
            vm.SetSelectedSessions([a]);
            await ExecuteAsync(vm.CopyUrlCommand);
            await ExecuteAsync(vm.FilterByHostCommand);
            await ExecuteAsync(vm.ExcludeHostCommand);
            await ExecuteAsync(vm.OpenExclusionSummaryCommand);
            await ExecuteAsync(vm.OpenHttpsDecryptHostsCommand);
            await ExecuteAsync(vm.OpenLoopbackExemptCommand);
            await ExecuteAsync(vm.OpenSessionRetentionCommand);
            await ExecuteAsync(vm.OpenLoggingSettingsCommand);
            await ExecuteAsync(vm.ImportHarCommand);
            await ExecuteAsync(vm.ImportArchiveCommand);

            vm.IgnoreServerCertificateErrors = true;
            vm.IgnoreServerCertificateErrors = true;
            vm.AutoStartCapture = vm.AutoStartCapture;
            vm.SearchQuery = "host:a.test";
            vm.SearchQuery = "host:a.test";

            await ExecuteAsync(vm.LoadIntoComposerCommand);
            StringAssert.Contains(vm.StatusText, "Composer");
            vm.SelectedSession = null;
            vm.SetSelectedSessions([]);
            await ExecuteAsync(vm.LoadIntoComposerCommand);
            StringAssert.Contains(vm.StatusText, "Select a session");
            await ExecuteAsync(vm.ReplayCommand);
            StringAssert.Contains(vm.StatusText, "Select a session");
            await ExecuteAsync(vm.FilterByProcessCommand);
            StringAssert.Contains(vm.StatusText, "process");
            await ExecuteAsync(vm.OpenAboutCommand);

            vm.AutoResponder.SelectedRule = null;
            await ExecuteAsync(vm.UpdateAutoResponderRuleCommand);
            await ExecuteAsync(vm.DeleteAutoResponderRuleCommand);
            vm.MapRemote.SelectedRule = null;
            await ExecuteAsync(vm.UpdateMapRemoteRuleCommand);
            await ExecuteAsync(vm.DeleteMapRemoteRuleCommand);

            vm.SetTransientStatus("transient-cov", StatusSeverity.Success, toastImportant: true, revertMs: 50);
            await Task.Delay(80);
            vm.SetTransientStatus("zero-revert", StatusSeverity.Neutral, revertMs: 0);
            await vm.TryAutoStartAsync();

            await ExecuteAsync(vm.StartCaptureCommand);
            Assert.IsTrue(interception.IsRunning, vm.StatusText);
            await ExecuteAsync(vm.ToggleCapturingCommand);
            await ExecuteAsync(vm.ToggleDecryptHttpsCommand);
            await ExecuteAsync(vm.OpenToolsComposerCommand);
            await ExecuteAsync(vm.OpenToolsBreakpointsCommand);
            await ExecuteAsync(vm.OpenToolsAutoResponderCommand);
            await ExecuteAsync(vm.ClearFiltersCommand);
            vm.SetSelectedSessions([a]);
            await ExecuteAsync(vm.RemoveSelectedSessionsCommand);
            await ExecuteAsync(vm.StopCaptureCommand);
            Assert.IsFalse(interception.IsRunning);
            await ExecuteAsync(vm.ToggleInterceptCommand);
            if (interception.IsRunning)
                await ExecuteAsync(vm.StopCaptureCommand);

            typeof(MainWindowViewModel).GetMethod("ApplyExclusionSettingsFromSettings",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);

            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var trustType = typeof(MainWindowViewModel);
            _ = trustType.GetMethod("FormatOsTrustFailureStatus", flags)!.Invoke(null,
                [CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, "nope")]);
            _ = trustType.GetMethod("FormatOsTrustFailureStatus", flags)!.Invoke(null, [null]);
            _ = trustType.GetMethod("FormatUntrustStillPresentStatus", flags)!.Invoke(null, null);
            _ = trustType.GetMethod("FormatUntrustRemovedStatus", flags)!.Invoke(null, null);
            _ = trustType.GetMethod("FormatRotateCaTrustedStatus", flags)!.Invoke(null, [true]);
            _ = trustType.GetMethod("FormatRotateCaTrustedStatus", flags)!.Invoke(null, [false]);
            _ = trustType.GetMethod("FormatRotateCaDeferredTrustStatus", flags)!.Invoke(null, [true]);
            _ = trustType.GetMethod("FormatRotateCaDeferredTrustStatus", flags)!.Invoke(null, [false]);
            _ = trustType.GetMethod("FormatFirefoxTrustOutcome", flags)!.Invoke(null,
                [CertificateOsTrustResult.Ok("trusted")]);
            _ = trustType.GetMethod("FormatFirefoxTrustOutcome", flags)!.Invoke(null,
                [CertificateOsTrustResult.Fail(CertificateOsTrustKind.CertutilMissing, "need certutil")]);
            _ = trustType.GetMethod("FormatFirefoxTrustOutcome", flags)!.Invoke(null,
                [CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, "firefox down")]);

            var proc = new SessionSnapshot
            {
                Id = 9, Method = "GET", Url = "https://p.test/", Host = "p.test",
                ProcessName = "chrome", ProcessId = 42,
            };
            vm.SeedSession(proc);
            vm.SelectedSession = proc;
            vm.SetSelectedSessions([proc]);
            await ExecuteAsync(vm.FilterByProcessCommand);
            StringAssert.Contains(vm.StatusText, "chrome");
            await ExecuteAsync(vm.CheckForUpdatesCommand);

            vm.PersistSessionGridLayout(new SessionGridLayoutDto { SortColumnKey = "Id" });
            Assert.AreEqual("Id", vm.GetSessionGridLayout()?.SortColumnKey);
            vm.NotifyThemeVariantChanged();
            vm.ReportActionFailure(new OperationCanceledException());
            vm.ReportActionFailure(new InvalidOperationException("cov-fail"));
            StringAssert.Contains(vm.StatusText, "Action failed");

            var flagsVm = BindingFlags.NonPublic | BindingFlags.Static;
            Assert.AreEqual(IPAddress.Any,
                (IPAddress)typeof(MainWindowViewModel).GetMethod("ParseBindAddress", flagsVm)!.Invoke(null, ["0.0.0.0"])!);
            Assert.AreEqual(IPAddress.Any,
                (IPAddress)typeof(MainWindowViewModel).GetMethod("ParseBindAddress", flagsVm)!.Invoke(null, ["  "])!);
            Assert.AreEqual(IPAddress.Loopback,
                (IPAddress)typeof(MainWindowViewModel).GetMethod("ParseBindAddress", flagsVm)!.Invoke(null, ["127.0.0.1"])!);
            Assert.AreEqual("h.test",
                (string?)typeof(MainWindowViewModel).GetMethod("TryHost", flagsVm)!.Invoke(null, ["https://h.test/x"]));
            Assert.IsNull(typeof(MainWindowViewModel).GetMethod("TryHost", flagsVm)!.Invoke(null, ["not-a-url"]));
            Assert.AreEqual("hi",
                (string)typeof(MainWindowViewModel).GetMethod("Truncate", flagsVm)!.Invoke(null, ["hi", 10])!);
            Assert.AreEqual("hello…",
                (string)typeof(MainWindowViewModel).GetMethod("Truncate", flagsVm)!.Invoke(null, ["hello world", 5])!);
            _ = typeof(MainWindowViewModel).GetMethod("SystemProxyEnabledStatusMessage", flagsVm)!.Invoke(null, []);
            _ = typeof(MainWindowViewModel).GetMethod("DescribePanel", flagsVm)!
                .Invoke(null, [new { Title = "Tools", Description = "cov" }]);
            _ = typeof(MainWindowViewModel).GetMethod("DescribePanel", flagsVm)!.Invoke(null, [new object()]);

            var proto = new SessionSnapshot
            {
                Id = 12, Method = "GET", Url = "wss://ws.test/", Host = "ws.test",
                IsWebSocket = true, IsServerSentEvents = true, IsGrpc = true,
                SseEvents = [new SseEventSnapshot { Data = "x" }],
                ProtobufDecodedText = "{}",
            };
            vm.SeedSession(proto);
            vm.SelectedSession = proto;
            vm.SelectedInspectTabIndex = 4;
            vm.SelectedInspectTabIndex = 5;
            vm.SelectedInspectTabIndex = 6;

            vm.ShowSessionDetails = true;
            vm.SelectedInspectTabIndex = 1;
            vm.SelectedInspectTabIndex = 2;
            vm.SelectedInspectTabIndex = 4;

            await ExecuteAsync(vm.ExportSelectedHarCommand);
            await ExecuteAsync(vm.ExportSelectedArchiveCommand);
            await ExecuteAsync(vm.RemoveSelectedSessionsCommand);
            vm.SeedSession(proc);
            vm.SetSelectedSessions([proc]);
            await ExecuteAsync(vm.ClearSessionsCommand);
            await ExecuteAsync(vm.ToggleSystemProxyCommand);
            await ExecuteAsync(vm.OpenToolsScriptsCommand);

            var notifier = new AvaloniaStatusNotifier(() => null);
            foreach (var severity in Enum.GetValues<StatusSeverity>())
                notifier.Show("toast " + severity, severity);
            notifier.Show("   ", StatusSeverity.Neutral);
            notifier.Show("", StatusSeverity.Error);

            Assert.IsFalse(MapRemoteViewModel.TryApplyRewrite("https://a/", "https://a/", "", out _));
            Assert.IsFalse(MapRemoteViewModel.TryApplyRewrite("https://a/", "*", "not-a-url", out _));
            Assert.IsTrue(MapRemoteViewModel.TryApplyRewrite("https://a/x", "*", "https://b.test/", out var star));
            Assert.AreEqual("https://b.test/", star);
            Assert.IsTrue(MapRemoteViewModel.TryApplyRewrite("https://a/x", "*", "https://b.test/*", out var embed));
            StringAssert.Contains(embed, "https://a/x");
            Assert.IsTrue(MapRemoteViewModel.TryApplyRewrite("https://a/api/v1?q=1", "https://a/*", "https://b.test/*", out var cap));
            StringAssert.Contains(cap, "api/v1");
            Assert.IsTrue(MapRemoteViewModel.TryApplyRewrite("https://a/api", "https://a/*", "https://b.test/fixed", out var lit));
            Assert.AreEqual("https://b.test/fixed", lit);

            var map = new MapRemoteViewModel { Enabled = true };
            map.Rules.Add(new MapRemoteRule { Enabled = true, MatchUrl = "*rewrite*", TargetUrl = "https://z.test/" });
            Assert.IsTrue(map.TryRewrite("https://a/rewrite", out var rewritten, out var matched));
            Assert.AreEqual("https://z.test/", rewritten);
            Assert.IsNotNull(matched);
            map.Enabled = false;
            Assert.IsFalse(map.TryRewrite("https://a/rewrite", out _, out _));

            _ = OsTrustUxCopy.ConfirmInstallRootCaBody();
            _ = OsTrustUxCopy.ConfirmRemoveRootCaBody();
            _ = OsTrustUxCopy.ConfirmElevateRootCaBody();
            _ = OsTrustUxCopy.TrustRecoveryAdminBody("msg");
            _ = OsTrustUxCopy.ExcludedHostsIntro();
            _ = OsTrustUxCopy.ExcludedHostsLoopbackHint();
            _ = OsTrustUxCopy.FormatDecryptTrustFailed(
                CertificateOsTrustResult.Fail(CertificateOsTrustKind.HomebrewMissing, ""));
            _ = OsTrustUxCopy.FormatDecryptTrustFailed(
                CertificateOsTrustResult.Fail(CertificateOsTrustKind.CertutilMissing, "need tools"));
            _ = OsTrustUxCopy.FormatDecryptTrustFailed(
                CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, ""));
            _ = OsTrustUxCopy.FormatDecryptTrustFailed(null);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task ViewModel_OpaqueHintExclusionStatusAndShutdownArms()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-vm-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            settings.Current.AutoStartCapture = false;
            settings.Current.SystemProxyBypassHosts = ["localhost"];
            settings.Current.DecryptSkipHosts = ["pin.test"];
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

            var notifier = new RecordingStatusNotifier();
            vm.AttachStatusNotifier(notifier);
            vm.SetStatus("busy-cov", StatusSeverity.Busy);
            Assert.IsTrue(vm.IsStatusBusy);
            vm.SetStatus("idle-cov", StatusSeverity.Neutral);
            Assert.IsFalse(vm.IsStatusBusy);

            Assert.IsTrue(vm.HasExclusionSummary);
            StringAssert.Contains(vm.ExclusionSummaryText, "Exclusions");

            var tunnel = new SessionSnapshot
            {
                Id = 9,
                Method = "CONNECT",
                Url = "tunnel.example:443",
                Host = "tunnel.example",
                IsTunnel = true,
                OpaqueReason = OpaqueTunnelReason.DecryptOff,
            };
            vm.SeedSession(tunnel);
            vm.SelectedSession = tunnel;
            Assert.IsTrue(vm.ShowSelectedOpaqueHint);
            Assert.IsFalse(string.IsNullOrEmpty(vm.SelectedOpaqueHint));

            _ = vm.PlusPanelsSummary;
            _ = vm.EndpointStatusText;
            _ = vm.InterceptToggleText;
            _ = vm.BindFieldsEnabled;
            vm.BreakpointOnResponse = true;
            Assert.IsTrue(vm.BreakpointOnResponse);
            vm.ComposerMethod = "POST";
            vm.ComposerUrl = "https://c.test/";
            vm.ComposerHeaders = "X: 1";
            vm.ComposerBody = "b";
            Assert.AreEqual("POST", vm.ComposerMethod);

            vm.BeginBackgroundShutdown();
            await Task.Delay(50);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static async Task ExecuteAsync(ICommand command)
    {
        command.Execute(null);
        await Task.Delay(120);
    }

    [TestMethod]
    public void SelectedInspectFormatters_AndBindDebugHelpers_CoverBranches()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var vmType = typeof(MainWindowViewModel);

        var headers = (string)vmType.GetMethod("BuildSelectedHeadersText", flags)!
            .Invoke(null, [new SessionSnapshot
            {
                IsTunnel = true,
                OpaqueReason = OpaqueTunnelReason.DecryptOff,
                IsTranscoded = true,
                Method = "POST",
                Url = "https://api.test/v1?q=1",
                ClientMethod = "POST",
                ClientPathAndQuery = "/v1?q=1",
                ClientContentType = "application/json",
                UpstreamMethod = "POST",
                UpstreamPath = "/pkg.Svc/Method",
                UpstreamContentType = "application/grpc",
                RequestHeadersText = "Cookie: a=1\r\nHost: api.test\r\n",
                ResponseHeadersText = "Content-Type: application/json\r\n",
            }])!;
        StringAssert.Contains(headers, "=== Request ===");
        StringAssert.Contains(headers, "=== Response ===");
        StringAssert.Contains(headers, "gRPC-JSON");
        StringAssert.Contains(headers, "=== Cookies ===");
        StringAssert.Contains(headers, "=== Query ===");

        var plainHeaders = (string)vmType.GetMethod("BuildSelectedHeadersText", flags)!
            .Invoke(null, [new SessionSnapshot
            {
                RequestHeadersText = "Accept: */*\r\n",
                Url = "https://plain.test/",
            }])!;
        Assert.IsFalse(plainHeaders.Contains("=== Cookies ===", StringComparison.Ordinal));

        var append = vmType.GetMethod("AppendNameValues", flags)!;
        var sb = new System.Text.StringBuilder();
        append.Invoke(null, [sb, "=== Empty ===", new Dictionary<string, string>()]);
        Assert.AreEqual(0, sb.Length);
        append.Invoke(null, [sb, "=== Pair ===", new Dictionary<string, string> { ["k"] = "v" }]);
        StringAssert.Contains(sb.ToString(), "k=v");

        var body = (string)vmType.GetMethod("BuildSelectedBodyText", flags)!
            .Invoke(null, [new SessionSnapshot
            {
                IsTranscoded = true,
                RequestBodyText = "{\"a\":1}",
                ResponseBodyText = "{\"ok\":true}",
                UpstreamRequestBodyBytes = [1, 2],
                GrpcFrames = [new GrpcFrameSnapshot { Compressed = false, Length = 2, HexPreview = "0102" }],
            }])!;
        StringAssert.Contains(body, "Client (JSON/REST)");
        StringAssert.Contains(body, "Upstream gRPC frames");

        Assert.AreEqual("(no frames parsed)",
            (string)vmType.GetMethod("BuildSelectedFramesText", flags)!
                .Invoke(null, [new SessionSnapshot { IsWebSocket = true }])!);
        StringAssert.Contains(
            (string)vmType.GetMethod("BuildSelectedFramesText", flags)!
                .Invoke(null, [new SessionSnapshot
                {
                    WebSocketFrames =
                    [
                        new WebSocketFrameSnapshot
                        {
                            Direction = "Client", Opcode = "Text", PayloadPreview = "hi"
                        }
                    ]
                }])!,
            "hi");

        Assert.AreEqual("(no events parsed)",
            (string)vmType.GetMethod("BuildSelectedSseText", flags)!
                .Invoke(null, [new SessionSnapshot { IsServerSentEvents = true }])!);
        StringAssert.Contains(
            (string)vmType.GetMethod("BuildSelectedSseText", flags)!
                .Invoke(null, [new SessionSnapshot
                {
                    SseEvents = [new SseEventSnapshot { Event = "msg", Id = "1", Data = "ping" }]
                }])!,
            "ping");

        Assert.AreEqual("{}",
            (string)vmType.GetMethod("BuildSelectedProtobufText", flags)!
                .Invoke(null, [new SessionSnapshot { ProtobufDecodedText = "{}" }])!);
        _ = (string)vmType.GetMethod("BuildSelectedProtobufText", flags)!
            .Invoke(null, [new SessionSnapshot { IsGrpc = true, ResponseBodyBytes = [0] }])!;

        Assert.AreEqual("text/plain",
            (string?)vmType.GetMethod("GuessContentType", flags)!
                .Invoke(null, ["Content-Type: text/plain\r\n"])!);
        Assert.IsNull(vmType.GetMethod("GuessContentType", flags)!.Invoke(null, ["Accept: */*\r\n"]));

        Assert.IsTrue((bool)vmType.GetMethod("IsDebugFileLoggingEnabled", flags)!
            .Invoke(null, [new InspectorSettings { LoggingEnableFile = true, LoggingMinimumLevel = "Debug" }])!);
        Assert.IsFalse((bool)vmType.GetMethod("IsDebugFileLoggingEnabled", flags)!
            .Invoke(null, [new InspectorSettings { LoggingEnableFile = true, LoggingMinimumLevel = "Error" }])!);

        var dir = Path.Combine(Path.GetTempPath(), "ti-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
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
                BindPort = 8888,
                BindAddress = "0.0.0.0",
            };
            Assert.AreEqual("0.0.0.0",
                (string)vmType.GetMethod("FormatBindDisplay", flags)!.Invoke(vm, null)!);
            vm.BindAddress = "127.0.0.1";
            Assert.AreEqual("127.0.0.1",
                (string)vmType.GetMethod("FormatBindDisplay", flags)!.Invoke(vm, null)!);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void ResolveSessionHostAndProcess_CoverHostUrlAndPidFallbacks()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var host = typeof(MainWindowViewModel).GetMethod("ResolveSessionHost", flags)!;
        var process = typeof(MainWindowViewModel).GetMethod("ResolveSessionProcess", flags)!;

        Assert.AreEqual("from-host",
            (string?)host.Invoke(null, [new SessionSnapshot { Host = "  from-host  " }]));
        Assert.AreEqual("url.example",
            (string?)host.Invoke(null, [new SessionSnapshot { Url = "https://url.example/x" }]));
        Assert.IsNull(host.Invoke(null, [new SessionSnapshot { Url = "not-a-url" }]));

        Assert.AreEqual("chrome",
            (string?)process.Invoke(null, [new SessionSnapshot { ProcessName = "  chrome  " }]));
        Assert.AreEqual("4242",
            (string?)process.Invoke(null, [new SessionSnapshot { ProcessId = 4242 }]));
        Assert.IsNull(process.Invoke(null, [new SessionSnapshot()]));
    }
}
