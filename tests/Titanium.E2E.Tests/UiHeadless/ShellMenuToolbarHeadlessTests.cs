using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiHeadless;

[TestClass]
public class ShellMenuToolbarHeadlessTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task Shell_Search_AndToolsMenus_ReachControls()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            fx.Robot.SetText("SearchBox", "method:GET");
            Assert.AreEqual("method:GET", fx.ViewModel.SearchQuery);

            fx.Robot.Click("MenuToolsComposer");
            Assert.IsTrue(fx.ViewModel.ShowSessionDetails);
            Assert.AreEqual(1, fx.ViewModel.SelectedOuterPaneIndex);
            Assert.AreEqual(0, fx.ViewModel.SelectedToolsTabIndex);

            fx.Robot.Click("MenuToolsBreakpoints");
            Assert.AreEqual(1, fx.ViewModel.SelectedToolsTabIndex);

            fx.Robot.Click("MenuToolsAutoResponder");
            Assert.AreEqual(2, fx.ViewModel.SelectedToolsTabIndex);

            fx.Robot.Click("MenuToolsScripts");
            Assert.AreEqual(3, fx.ViewModel.SelectedToolsTabIndex);

            Assert.IsTrue(fx.Robot.TryFind<Avalonia.Controls.TextBlock>("StatusText", out _));
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task Capture_StartStop_AndClear_ViaAutomationIds()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(async () =>
        {
            fx.Robot.Click("MenuStartCapture");
            await InspectorUiRobot.WaitForAsync(() => fx.Interception.IsRunning, TimeSpan.FromSeconds(10));

            fx.ViewModel.Sessions.Add(new SessionSnapshot
            {
                Id = 42,
                Method = "GET",
                StatusCode = 200,
                Host = "example.test",
                Url = "http://example.test/",
                Protocol = "HTTP/1.1",
            });
            Assert.IsTrue(fx.ViewModel.Sessions.Count >= 1);

            fx.Robot.Click("MenuClearSessions");
            Assert.AreEqual(0, fx.ViewModel.Sessions.Count);

            fx.Robot.Click("MenuStopCapture");
            await InspectorUiRobot.WaitForAsync(() => !fx.Interception.IsRunning, TimeSpan.FromSeconds(10));
        });
    }
}
