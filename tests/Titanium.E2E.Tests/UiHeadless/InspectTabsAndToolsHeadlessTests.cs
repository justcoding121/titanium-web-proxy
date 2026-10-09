using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.E2E.Tests.UiHeadless;

[TestClass]
public class InspectTabsAndToolsHeadlessTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task InspectTabs_CycleRequestAndResponseHeadersAndBodies()
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
            fx.Window.UpdateLayout();
            Assert.IsFalse(fx.ViewModel.InspectHeadersCollapsed, "Default window keeps headers open");
            Assert.IsTrue(fx.Robot.Find<Avalonia.Controls.TextBox>("ReqHeadersText").IsVisible);

            fx.Robot.Click("TabRequest");
            Assert.AreEqual((int)InspectTab.Request, fx.ViewModel.SelectedInspectTabIndex);
            Assert.IsTrue(fx.Robot.TryFind<Avalonia.Controls.Control>("ReqHeadersText", out _));
            Assert.IsTrue(fx.Robot.TryFind<Avalonia.Controls.Control>("ReqBodyText", out _));

            fx.Robot.Click("TabResponse");
            Assert.AreEqual((int)InspectTab.Response, fx.ViewModel.SelectedInspectTabIndex);
            Assert.IsTrue(fx.Robot.TryFind<Avalonia.Controls.Control>("RespHeadersText", out _));
            Assert.IsTrue(fx.Robot.TryFind<Avalonia.Controls.Control>("RespBodyText", out _));
            fx.Robot.SetCheck("BodyHex", true);
            Assert.IsTrue(fx.ViewModel.BodyHexMode);
            Assert.IsFalse(fx.ViewModel.BodyPrettyEnabled);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task InspectHeadersSplit_CollapsesOnShortWindow_AndUserCanExpand()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(windowHeight: 480);
        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.Sessions.Add(new SessionSnapshot
            {
                Id = 8,
                Method = "GET",
                StatusCode = 200,
                Host = "127.0.0.1",
                Url = "http://127.0.0.1/inspect",
                Protocol = "HTTP/1.1",
                RequestHeadersText = "Host: 127.0.0.1\r\nAccept: */*\r\n",
            });
            fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];
            fx.Window.UpdateLayout();

            var pane = fx.Robot.Find<Avalonia.Controls.Control>("TabInspectHost");
            Assert.IsTrue(pane.Bounds.Height > 0, "Inspect pane was not measured");
            Assert.IsTrue(
                pane.Bounds.Height < MainWindowViewModel.ShortInspectPaneHeight,
                $"Expected a short inspect pane, was {pane.Bounds.Height}");
            Assert.IsTrue(fx.ViewModel.InspectHeadersCollapsed, "Short window collapses headers");
            Assert.IsFalse(fx.Robot.Find<Avalonia.Controls.TextBox>("ReqHeadersText").IsVisible);

            fx.Robot.Click("ToggleReqHeadersPane");
            Assert.IsFalse(fx.ViewModel.InspectHeadersCollapsed, "User can expand headers on a short window");
            Assert.IsTrue(fx.Robot.Find<Avalonia.Controls.TextBox>("ReqHeadersText").IsVisible);
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

        // Pump dispatcher so async RelayCommand continuations (no ConfigureAwait(false)) run.
        await fx.WaitUntilAsync(
            () => fx.ViewModel.StatusText.Contains("Exported 1 sessions", StringComparison.Ordinal)
                  || fx.ViewModel.StatusText.Contains("Export HAR failed", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20));

        await fx.DispatchAsync(() =>
        {
            Assert.IsTrue(fx.PathPicker.SaveCalls >= 1, "Save picker calls=" + fx.PathPicker.SaveCalls);
            Assert.IsTrue(File.Exists(har), "HAR should be written via scripted picker path");
            StringAssert.Contains(
                fx.ViewModel.StatusText,
                "Exported 1 sessions",
                "StatusText after HAR export: " + fx.ViewModel.StatusText);
        });

        try { File.Delete(har); } catch { /* ignore */ }
    }
}
