using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class AutoResponderAndSelectionGuardTests
{
    [TestMethod]
    public void AutoResponder_LoadToDtos_TryMatchAndDisplay()
    {
        var vm = new AutoResponderViewModel();
        vm.LoadFromDtos(
        [
            new AutoResponderRuleDto
            {
                MatchUrl = "https://api.*/x",
                StatusCode = 418,
                Body = "teapot",
                ContentType = "",
                Enabled = true,
            },
            new AutoResponderRuleDto
            {
                MatchUrl = "*",
                StatusCode = 200,
                Body = "off",
                ContentType = "text/html",
                Enabled = false,
            },
        ]);
        Assert.AreEqual(2, vm.Rules.Count);
        Assert.AreEqual("text/plain", vm.Rules[0].ContentType); // empty → default
        Assert.AreEqual("text/html", vm.Rules[1].ContentType);

        var dtos = vm.ToDtos();
        Assert.AreEqual(2, dtos.Count);
        Assert.AreEqual(418, dtos[0].StatusCode);

        Assert.IsFalse(vm.TryMatch("https://api.example/x", out _));
        vm.Enabled = true;
        Assert.IsTrue(vm.TryMatch("https://api.example/x", out var matched));
        Assert.AreEqual(418, matched!.StatusCode);
        Assert.IsTrue(vm.TryRespond(new SessionSnapshot { Url = "https://api.example/x" }, out _));
        Assert.IsFalse(vm.TryMatch("https://other/x", out _));

        vm.SelectedRule = vm.Rules[0];
        Assert.AreSame(vm.Rules[0], vm.SelectedRule);
        StringAssert.StartsWith(vm.Rules[0].Display, "✓");
        vm.Rules[1].Enabled = false;
        StringAssert.StartsWith(vm.Rules[1].Display, "✗");

        var notified = false;
        vm.RulesChanged += (_, _) => notified = true;
        vm.NotifyRulesChanged();
        Assert.IsTrue(notified);

        // empty / * filter
        vm.Rules.Clear();
        vm.Rules.Add(new AutoResponderRule { MatchUrl = "*", Enabled = true, StatusCode = 204 });
        Assert.IsTrue(vm.TryMatch("https://anything", out var star));
        Assert.AreEqual(204, star!.StatusCode);
        vm.Rules[0].MatchUrl = "";
        Assert.IsTrue(vm.TryMatch("https://anything", out _));
    }

    [TestMethod]
    public void MainWindowViewModel_NullSelectionGuards()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-insp-guards-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            Assert.IsNull(vm.SelectedSession);
            vm.LoadFromSelectedCommand.Execute(null);
            StringAssert.Contains(vm.StatusText, "Select a session");

            vm.LoadIntoComposerCommand.Execute(null);
            StringAssert.Contains(vm.StatusText, "Select a session");

            vm.CopyUrlCommand.Execute(null);
            StringAssert.Contains(vm.StatusText, "Select a session with a URL to copy");

            vm.ReplayCommand.Execute(null);
            // Guard path sets StatusText before any await, so no delay is required.
            StringAssert.Contains(vm.StatusText, "Select a session");

            vm.DeleteAutoResponderRuleCommand.Execute(null);
            StringAssert.Contains(vm.StatusText, "Select an AutoResponder rule");

            vm.UpdateAutoResponderRuleCommand.Execute(null);
            StringAssert.Contains(vm.StatusText, "Select an AutoResponder rule");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task LoadIntoComposer_PopulatesFieldsAndOpensToolsTab()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-insp-composer-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            vm.SeedSession(new SessionSnapshot
            {
                Id = 42,
                Method = "POST",
                Url = "https://api.example/v1",
                RequestHeadersText = "Content-Type: application/json\r\n",
                RequestBodyText = "{\"x\":1}",
                StatusCode = 200,
            });
            vm.SelectedSession = vm.Sessions[0];

            vm.LoadIntoComposerCommand.Execute(null);
            await Task.Delay(50);

            Assert.AreEqual("POST", vm.ComposerMethod);
            Assert.AreEqual("https://api.example/v1", vm.ComposerUrl);
            Assert.AreEqual("{\"x\":1}", vm.ComposerBody);
            Assert.IsTrue(vm.ShowSessionDetails);
            Assert.AreEqual(1, vm.SelectedOuterPaneIndex);
            Assert.AreEqual(0, vm.SelectedToolsTabIndex);
            StringAssert.Contains(vm.StatusText, "Composer loaded");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task CopyUrl_WithSelection_SetsCopiedStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-insp-copyurl-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            vm.SeedSession(new SessionSnapshot
            {
                Id = 7,
                Method = "GET",
                Url = "https://keep.example/",
                StatusCode = 200,
            });
            vm.SelectedSession = vm.Sessions[0];

            vm.CopyUrlCommand.Execute(null);
            await Task.Delay(50);

            StringAssert.Contains(vm.StatusText, "Copied URL");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void InterceptionService_ApplyHttpProtocolsAndConfigureLogging_WhenNotStarted()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController());
        Assert.IsFalse(interception.IsRunning);

        interception.ApplyHttpProtocols();
        Assert.IsTrue(interception.Http2Enabled);
        Assert.AreEqual(InterceptionService.IsHttp3Supported, interception.Http3Enabled);

        interception.ConfigureLogging(new InspectorSettings { LoggingEnabled = true, LoggingMinimumLevel = "Debug" });
        Assert.IsFalse(interception.IsRunning);
    }
}
