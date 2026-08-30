using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiHeadless;

[TestClass]
public class InspectTabsAndToolsHeadlessTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task InspectTabs_CycleHeadersBodyHex()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.Sessions.Add(new SessionSnapshot
            {
                Id = 7,
                Method = "GET",
                StatusCode = 200,
                Host = "127.0.0.1",
                Url = "http://127.0.0.1/inspect",
                Protocol = "HTTP/1.1",
                RequestHeadersText = "Host: 127.0.0.1",
                ResponseBodyText = "{\"ok\":true}",
            });
            fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];
            Assert.IsTrue(fx.ViewModel.ShowSessionDetails);

            fx.Robot.Click("TabHeaders");
            Assert.AreEqual(0, fx.ViewModel.SelectedInspectTabIndex);

            fx.Robot.Click("TabBody");
            Assert.AreEqual(1, fx.ViewModel.SelectedInspectTabIndex);

            fx.Robot.Click("TabHex");
            Assert.AreEqual(2, fx.ViewModel.SelectedInspectTabIndex);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task Composer_And_AutoResponder_Fields_RoundTrip()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            fx.Robot.Click("MenuToolsComposer");
            fx.Robot.SetText("ComposerMethod", "POST");
            fx.Robot.SetText("ComposerUrl", "http://127.0.0.1/echo");
            fx.Robot.SetText("ComposerHeaders", "Content-Type: application/json");
            fx.Robot.SetText("ComposerBody", "{\"a\":1}");
            Assert.AreEqual("POST", fx.ViewModel.ComposerMethod);
            Assert.AreEqual("http://127.0.0.1/echo", fx.ViewModel.ComposerUrl);

            fx.Robot.Click("MenuToolsAutoResponder");
            fx.Robot.SetCheck("AutoResponderEnabled", true);
            fx.Robot.SetText("AutoResponderMatch", "*/echo");
            fx.Robot.SetText("AutoResponderStatus", "209");
            fx.Robot.SetText("AutoResponderContentType", "text/plain");
            fx.Robot.SetText("AutoResponderBody", "stub");
            fx.Robot.Click("AutoResponderAdd");
            Assert.IsTrue(fx.ViewModel.AutoResponder.Rules.Count >= 1);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task FileExportHar_UsesScriptedPathPicker()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        var har = Path.Combine(Path.GetTempPath(), "twp-har-" + Guid.NewGuid().ToString("N") + ".har");
        fx.PathPicker.SavePath = har;
        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.SeedSession(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                StatusCode = 200,
                Host = "h",
                Url = "http://h/",
                Protocol = "HTTP/1.1",
            });
            fx.Robot.Click("MenuExportHar");
        });

        // ExportHarCommand is async; pump dispatcher while waiting so StatusText continuations run.
        await fx.WaitUntilAsync(
            () => fx.ViewModel.StatusText.Contains("Exported 1 sessions", StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));

        await fx.DispatchAsync(() =>
        {
            Assert.IsTrue(fx.PathPicker.SaveCalls >= 1);
            Assert.IsTrue(File.Exists(har), "HAR should be written via scripted picker path");
            StringAssert.Contains(fx.ViewModel.StatusText, "Exported 1 sessions");
        });

        try { File.Delete(har); } catch { /* ignore */ }
    }
}
