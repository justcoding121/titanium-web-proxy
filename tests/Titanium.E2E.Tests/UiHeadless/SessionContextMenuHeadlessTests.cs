using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiHeadless;

[TestClass]
public class SessionContextMenuHeadlessTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task ContextMenu_GroupsActionsUnderSubmenus()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();

        await fx.DispatchAsync(() =>
        {
            var snap = new SessionSnapshot
            {
                Id = 9,
                Method = "POST",
                StatusCode = 200,
                Host = "one.test",
                Url = "http://one.test/x",
                Protocol = "HTTP/1.1",
                RequestBodyText = "body",
                RequestBodyCapture = BodyCaptureState.Complete,
            };
            fx.ViewModel.SeedSession(snap);
            fx.ViewModel.SelectedSession = snap;
            fx.ViewModel.SetSelectedSessions([snap]);

            Assert.IsTrue(fx.ViewModel.CanSaveSessionMenu);
            Assert.IsTrue(fx.ViewModel.CanCopySessionMenu);
            Assert.IsTrue(fx.ViewModel.CanFilterSessionMenu);

            var menu = fx.Robot.Find<DataGrid>("SessionsGrid").ContextMenu!;
            Assert.IsNotNull(FindMenu(menu, "CtxSaveAs"));
            Assert.IsNotNull(FindMenu(menu, "CtxExport"));
            Assert.IsNotNull(FindMenu(menu, "CtxCopyAs"));
            Assert.IsNotNull(FindMenu(menu, "CtxFilterMenu"));
            Assert.IsNotNull(FindMenu(menu, "CtxCopyAsCurl"));
            Assert.IsNotNull(FindMenu(menu, "CtxFilterByHost"));
            Assert.IsNotNull(FindMenu(menu, "CtxSaveRequestBody"));
        });
    }

    private static MenuItem? FindMenu(ContextMenu root, string automationId) =>
        root.GetLogicalDescendants()
            .OfType<MenuItem>()
            .FirstOrDefault(m => AutomationProperties.GetAutomationId(m) == automationId);
}
