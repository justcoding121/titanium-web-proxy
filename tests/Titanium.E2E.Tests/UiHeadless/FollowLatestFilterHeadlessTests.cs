using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiHeadless;

[TestClass]
public class FollowLatestFilterHeadlessTests
{
    private const int RowCount = 300;

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    private static ScrollBar VerticalScroll(InspectorHeadlessFixture fx) =>
        fx.Robot.Find<DataGrid>("SessionsGrid")
            .GetVisualDescendants()
            .OfType<ScrollBar>()
            .First(b => b.Orientation == Orientation.Vertical);

    private static void AssertAtBottom(InspectorHeadlessFixture fx, string when)
    {
        var bar = VerticalScroll(fx);
        Assert.IsTrue(bar.Maximum > 0, $"{when}: grid should overflow (max {bar.Maximum}).");
        Assert.IsTrue(
            bar.Value >= bar.Maximum - SessionListFollowLatest.DefaultThresholdPx,
            $"{when}: expected the live edge (bottom) but value={bar.Value} max={bar.Maximum}.");
    }

    private static void Seed(InspectorHeadlessFixture fx, int id) =>
        fx.ViewModel.SeedSession(new SessionSnapshot
        {
            Id = id,
            Method = "GET",
            StatusCode = 200,
            Host = id % 2 == 0 ? "even.test" : "odd.test",
            Url = $"http://{(id % 2 == 0 ? "even" : "odd")}.test/{id}",
            Protocol = "HTTP/1.1",
        });

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task AddingAndClearingAFilter_KeepsFollowingTheLatestSession()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            for (var id = 1; id <= RowCount; id++)
            {
                Seed(fx, id);
            }

            Pump();
            AssertAtBottom(fx, "before filtering");

            fx.ViewModel.SearchQuery = "odd.test";
            Pump();
            AssertAtBottom(fx, "after adding a filter");

            fx.ViewModel.SearchQuery = "";
            Pump();
            AssertAtBottom(fx, "after clearing the filter");

            Seed(fx, RowCount + 1);
            Pump();
            AssertAtBottom(fx, "after a new session arrives");
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task AddingAndClearingAFilter_WithIdDescending_StaysAtTheTop()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            var grid = fx.Robot.Find<DataGrid>("SessionsGrid");
            grid.Columns.First(c => SessionGridLayout.GetColumnKey(c.Header) == "Id")
                .Sort(System.ComponentModel.ListSortDirection.Descending);
            Pump();

            for (var id = 1; id <= RowCount; id++)
            {
                Seed(fx, id);
            }

            Pump();
            fx.ViewModel.SearchQuery = "odd.test";
            Pump();
            fx.ViewModel.SearchQuery = "";
            Pump();
            Seed(fx, RowCount + 1);
            Pump();

            var bar = VerticalScroll(fx);
            Assert.IsTrue(
                bar.Value <= SessionListFollowLatest.DefaultThresholdPx,
                $"Id descending follows the top edge but value={bar.Value} max={bar.Maximum}.");
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task ClearingAFilter_DoesNotJumpWhenTheUserScrolledAway()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            for (var id = 1; id <= RowCount; id++)
            {
                Seed(fx, id);
            }

            Pump();
            var bar = VerticalScroll(fx);
            bar.Value = 0; // the user scrolls to the top: following pauses
            Pump();

            fx.ViewModel.SearchQuery = "odd.test";
            Pump();
            fx.ViewModel.SearchQuery = "";
            Pump();

            Seed(fx, RowCount + 1);
            Pump();

            bar = VerticalScroll(fx);
            Assert.IsTrue(
                bar.Value < bar.Maximum - SessionListFollowLatest.DefaultThresholdPx,
                "A user who scrolled away must not be pulled back to the bottom by filtering.");
        });
    }
}
