using System.Net;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

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
}
