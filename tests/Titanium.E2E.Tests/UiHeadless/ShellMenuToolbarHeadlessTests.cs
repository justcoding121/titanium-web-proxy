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
            Assert.IsTrue(fx.Robot.TryFind<Avalonia.Controls.TextBlock>("SessionCountText", out _));
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

            fx.ViewModel.SeedSession(new SessionSnapshot
            {
                Id = 42,
                Method = "GET",
                StatusCode = 200,
                Host = "example.test",
                Url = "http://example.test/",
                Protocol = "HTTP/1.1",
            });
            Assert.IsTrue(fx.ViewModel.Sessions.Count >= 1);
            Assert.IsTrue(fx.ViewModel.ClearSessionsCommand.CanExecute(null));

            fx.Robot.Click("MenuClearSessions");
            Assert.AreEqual(0, fx.ViewModel.Sessions.Count);

            fx.Robot.Click("MenuStopCapture");
            await InspectorUiRobot.WaitForAsync(() => !fx.Interception.IsRunning, TimeSpan.FromSeconds(10));
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task ClearSessions_DoesNotReopenClosedDetailsPane()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.SeedSession(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                StatusCode = 200,
                Host = "a.test",
                Url = "http://a.test/",
                Protocol = "HTTP/1.1",
            });
            fx.ViewModel.SeedSession(new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                StatusCode = 200,
                Host = "b.test",
                Url = "http://b.test/",
                Protocol = "HTTP/1.1",
            });
            fx.ViewModel.SeedSession(new SessionSnapshot
            {
                Id = 3,
                Method = "GET",
                StatusCode = 200,
                Host = "c.test",
                Url = "http://c.test/",
                Protocol = "HTTP/1.1",
            });

            fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[1];
            Assert.IsTrue(fx.ViewModel.ShowSessionDetails);

            fx.Robot.Click("CloseDetailsButton");
            Assert.IsFalse(fx.ViewModel.ShowSessionDetails);
            Assert.IsNotNull(fx.ViewModel.SelectedSession);

            Assert.IsTrue(fx.ViewModel.ClearSessionsCommand.CanExecute(null));
            fx.Robot.Click("MenuClearSessions");

            Assert.AreEqual(0, fx.ViewModel.Sessions.Count);
            Assert.IsNull(fx.ViewModel.SelectedSession);
            Assert.IsFalse(fx.ViewModel.ShowSessionDetails);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task MenuRemoveSelected_And_DeleteKey_RemoveSessions()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.SeedSession(new SessionSnapshot
            {
                Id = 11,
                Method = "GET",
                StatusCode = 200,
                Host = "del.test",
                Url = "http://del.test/one",
                Protocol = "HTTP/1.1",
            });
            fx.ViewModel.SeedSession(new SessionSnapshot
            {
                Id = 12,
                Method = "GET",
                StatusCode = 200,
                Host = "del.test",
                Url = "http://del.test/two",
                Protocol = "HTTP/1.1",
            });

            var first = fx.ViewModel.Sessions[0];
            fx.ViewModel.SelectedSession = first;
            fx.ViewModel.SetSelectedSessions([first]);
            Assert.IsTrue(fx.ViewModel.RemoveSelectedSessionsCommand.CanExecute(null));

            var beforeMenu = fx.ViewModel.Sessions.Count;
            fx.Robot.Click("MenuRemoveSelected");
            Assert.IsTrue(fx.ViewModel.Sessions.Count < beforeMenu, "MenuRemoveSelected must drop count");

            // Recreate two sessions for Delete-key single then multi.
            fx.ViewModel.SeedSession(new SessionSnapshot
            {
                Id = 21,
                Method = "GET",
                StatusCode = 200,
                Host = "del.test",
                Url = "http://del.test/a",
                Protocol = "HTTP/1.1",
            });
            fx.ViewModel.SeedSession(new SessionSnapshot
            {
                Id = 22,
                Method = "GET",
                StatusCode = 200,
                Host = "del.test",
                Url = "http://del.test/b",
                Protocol = "HTTP/1.1",
            });

            var one = fx.ViewModel.Sessions[0];
            fx.ViewModel.SelectedSession = one;
            fx.ViewModel.SetSelectedSessions([one]);
            Assert.IsTrue(fx.Robot.TryFind<Avalonia.Controls.DataGrid>("SessionsGrid", out var grid) && grid is not null);
            grid!.SelectedItems.Clear();
            grid.SelectedItems.Add(one);

            var beforeDelete = fx.ViewModel.Sessions.Count;
            fx.Robot.RaiseKey("SessionsGrid", Avalonia.Input.Key.Delete);
            Assert.IsTrue(fx.ViewModel.Sessions.Count < beforeDelete, "Delete key must remove selected session");

            if (fx.ViewModel.Sessions.Count < 2)
            {
                fx.ViewModel.SeedSession(new SessionSnapshot
                {
                    Id = 31,
                    Method = "GET",
                    StatusCode = 200,
                    Host = "del.test",
                    Url = "http://del.test/c",
                    Protocol = "HTTP/1.1",
                });
                fx.ViewModel.SeedSession(new SessionSnapshot
                {
                    Id = 32,
                    Method = "GET",
                    StatusCode = 200,
                    Host = "del.test",
                    Url = "http://del.test/d",
                    Protocol = "HTTP/1.1",
                });
            }

            var multi = fx.ViewModel.Sessions.Take(2).ToList();
            Assert.IsTrue(multi.Count >= 2);
            fx.ViewModel.SetSelectedSessions(multi);
            fx.ViewModel.SelectedSession = multi[0];
            grid.SelectedItems.Clear();
            foreach (var s in multi)
                grid.SelectedItems.Add(s);

            var beforeMulti = fx.ViewModel.Sessions.Count;
            Assert.IsTrue(fx.ViewModel.HasSelectedSessions);
            fx.Robot.RaiseKey("SessionsGrid", Avalonia.Input.Key.Delete);
            Assert.IsTrue(fx.ViewModel.Sessions.Count < beforeMulti, "Delete key must remove multi-selected sessions");
        });
    }
}
