using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiHeadless;

[TestClass]
public class SessionMarkHeadlessTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task Mark_PaintsLeftStripe_AndCommentShowsColumn()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        fx.Dialogs.CommentResult = "follow up";

        SessionSnapshot? snap = null;
        await fx.DispatchAsync(() =>
        {
            snap = new SessionSnapshot
            {
                Id = 42,
                Method = "GET",
                StatusCode = 200,
                Host = "mark.test",
                Url = "http://mark.test/",
                Protocol = "HTTP/1.1",
            };
            fx.ViewModel.SeedSession(snap);
            fx.ViewModel.SelectedSession = snap;
            fx.ViewModel.SetSelectedSessions([snap]);
        });

        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.SetMarkCommand.Execute("Green");
            Assert.AreEqual(SessionMark.Green, snap!.Mark);
            Assert.IsTrue(fx.ViewModel.StatusText.Contains("green", StringComparison.OrdinalIgnoreCase));

            var row = FindRow(fx, snap);
            Assert.IsNotNull(row);
            Assert.IsTrue(row!.Classes.Contains("mark-green"));
            Assert.AreEqual(4, row.BorderThickness.Left, 0.1);
            Assert.IsNotNull(row.BorderBrush);
        });

        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.CommentCommand.Execute(null);
            Assert.AreEqual("follow up", snap!.Comment);
            Assert.IsTrue(fx.ViewModel.IsGridColumnVisible("Comment"));
            Assert.IsTrue(fx.ViewModel.StatusText.Contains("Comment", StringComparison.OrdinalIgnoreCase));

            var row = FindRow(fx, snap);
            Assert.IsNotNull(row);
            Assert.IsTrue(row!.Classes.Contains("has-comment"));
            Assert.AreEqual("follow up", ToolTip.GetTip(row) as string);
        });
    }

    private static DataGridRow? FindRow(InspectorHeadlessFixture fx, SessionSnapshot snap)
    {
        Dispatcher.UIThread.RunJobs();
        return fx.Window.GetVisualDescendants()
            .OfType<DataGridRow>()
            .FirstOrDefault(r => ReferenceEquals(r.DataContext, snap));
    }
}
