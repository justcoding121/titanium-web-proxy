using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiHeadless;

[TestClass]
public class GridColumnsHeadlessTests
{
    private static readonly string[] OriginalColumnKeys =
        ["Id", "Method", "Status", "Host", "URL", "Protocol", "Duration", "TTFB", "Size", "Process"];

    private static readonly MethodInfo GetSortDescription = typeof(DataGridColumn).GetMethod(
        "GetSortDescription",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static DataGrid Grid(InspectorHeadlessFixture fx) => fx.Robot.Find<DataGrid>("SessionsGrid");

    private static string Key(DataGridColumn column) => SessionGridLayout.GetColumnKey(column.Header)!;

    private static DataGridColumn Column(DataGrid grid, string key) => grid.Columns.Single(c => Key(c) == key);

    private static string[] VisibleKeysInDisplayOrder(DataGrid grid) =>
        grid.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex).Select(Key).ToArray();

    private static string[] ExpectedDefaultKeys(InspectorHeadlessFixture fx) =>
        OriginalColumnKeys.Where(k => k != "Process" || fx.ViewModel.ShowProcessColumn).ToArray();

    private static void Pump() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    private static bool IsSorted(DataGridColumn column) => GetSortDescription.Invoke(column, null) is not null;

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task DefaultGrid_ShowsTheOriginalColumnsInOrder_AndHidesOptionalOnes()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            var grid = Grid(fx);
            CollectionAssert.AreEqual(ExpectedDefaultKeys(fx), VisibleKeysInDisplayOrder(grid));
            foreach (var key in new[] { "Started", "Scheme", "Content-Type" })
            {
                Assert.IsFalse(Column(grid, key).IsVisible, key);
            }
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task Catalog_MatchesGridColumns_AndMenuItems()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            var grid = Grid(fx);
            CollectionAssert.AreEqual(
                SessionGridColumnCatalog.All.Select(c => c.Key).ToArray(),
                grid.Columns.Select(Key).ToArray());

            foreach (var info in SessionGridColumnCatalog.All)
            {
                var item = fx.Robot.Find<MenuItem>(info.MenuAutomationId);
                Assert.AreEqual(info.Key, item.CommandParameter, info.MenuAutomationId);
                Assert.AreEqual(info.MenuLabel, item.Header, info.MenuAutomationId);
            }
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task OptionsColumnsMenu_TogglesColumn_Persists_AndKeepsCheckMarkInSync()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            var grid = Grid(fx);
            var item = fx.Robot.Find<MenuItem>("MenuColumn_Started");
            Assert.IsFalse(item.IsChecked);

            fx.Robot.Click("MenuColumn_Started");
            Assert.IsTrue(Column(grid, "Started").IsVisible);
            Assert.IsTrue(item.IsChecked);
            Assert.IsTrue(fx.ViewModel.GetSessionGridLayout()?.ColumnVisibility?["Started"]);

            fx.Robot.Click("MenuColumn_Started");
            Assert.IsFalse(Column(grid, "Started").IsVisible);
            Assert.IsFalse(item.IsChecked);
            Assert.IsNull(fx.ViewModel.GetSessionGridLayout()?.ColumnVisibility);

            // Original columns can be hidden too; URL cannot.
            fx.Robot.Click("MenuColumn_Host");
            Assert.IsFalse(Column(grid, "Host").IsVisible);
            Assert.IsFalse(fx.Robot.Find<MenuItem>("MenuColumn_URL").Command!.CanExecute("URL"));
            fx.ViewModel.SetGridColumnVisible("URL", false);
            Assert.IsTrue(Column(grid, "URL").IsVisible);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task HidingAndShowingAColumn_KeepsItsWidth()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            var grid = Grid(fx);
            fx.Robot.Click("MenuColumn_ContentType");
            var column = Column(grid, "Content-Type");
            column.Width = new DataGridLength(240);

            fx.Robot.Click("MenuColumn_ContentType");
            Assert.IsFalse(column.IsVisible);
            fx.Robot.Click("MenuColumn_ContentType");

            Assert.IsTrue(column.IsVisible);
            Assert.AreEqual(240, column.Width.Value);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task ResetColumns_RestoresDefaultVisibilityOrderWidthAndSort()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            var grid = Grid(fx);
            var defaultMethodWidth = Column(grid, "Method").Width;

            fx.Robot.Click("MenuColumn_Started");
            fx.Robot.Click("MenuColumn_Host");
            Column(grid, "Method").Width = new DataGridLength(200);
            Column(grid, "Started").Sort(ListSortDirection.Descending);
            Pump();

            fx.Robot.Click("MenuColumnsReset");
            Pump();

            CollectionAssert.AreEqual(ExpectedDefaultKeys(fx), VisibleKeysInDisplayOrder(grid));
            Assert.AreEqual(defaultMethodWidth.Value, Column(grid, "Method").Width.Value);
            Assert.IsNull(fx.ViewModel.GetSessionGridLayout());
            Assert.IsFalse(fx.Robot.Find<MenuItem>("MenuColumn_Started").IsChecked);
            Assert.IsTrue(fx.Robot.Find<MenuItem>("MenuColumn_Host").IsChecked);
            Assert.IsTrue(IsSorted(Column(grid, "Id")));
            Assert.IsFalse(IsSorted(Column(grid, "Started")));
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task HidingTheSortedColumn_FallsBackToIdAscending()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            var grid = Grid(fx);
            fx.Robot.Click("MenuColumn_Scheme");
            Column(grid, "Scheme").Sort(ListSortDirection.Ascending);
            Pump();
            Assert.IsTrue(IsSorted(Column(grid, "Scheme")));

            fx.Robot.Click("MenuColumn_Scheme");
            Pump();

            Assert.IsFalse(IsSorted(Column(grid, "Scheme")));
            Assert.IsTrue(IsSorted(Column(grid, "Id")));
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task HidingIdColumn_DoesNotChangeTheSort()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            var grid = Grid(fx);
            Column(grid, "Id").Sort(ListSortDirection.Ascending);
            Pump();
            Assert.IsTrue(IsSorted(Column(grid, "Id")), "Id ascending is the default sort.");
            fx.Robot.Click("MenuColumn_Id");
            Pump();

            Assert.IsFalse(Column(grid, "Id").IsVisible);
            Assert.IsTrue(IsSorted(Column(grid, "Id")));
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task OptionalColumns_ShowSessionValues()
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
                Host = "cols.test",
                Url = "https://cols.test/a",
                Protocol = "HTTP/1.1",
                ContentType = "application/json; charset=utf-8",
            });
            fx.Robot.Click("MenuColumn_Started");
            fx.Robot.Click("MenuColumn_Scheme");
            fx.Robot.Click("MenuColumn_ContentType");

            var row = fx.ViewModel.Sessions[0];
            Assert.AreEqual("https", row.Scheme);
            Assert.AreEqual("application/json", row.ContentTypeDisplay);
            Assert.IsFalse(string.IsNullOrEmpty(row.StartedDisplay));
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task HeaderContextMenu_ListsCatalogColumns_WithCurrentChecks()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            fx.Robot.Click("MenuColumn_Scheme");
            var menu = fx.Window.BuildColumnChooserMenu(fx.ViewModel);

            var items = menu.Items.OfType<MenuItem>().ToList();
            var chooser = items.Where(i => i.CommandParameter is string).ToList();
            var expected = SessionGridColumnCatalog.All
                .Where(c => !c.PlatformGated || fx.ViewModel.ShowProcessColumn)
                .Select(c => c.Key)
                .ToArray();
            CollectionAssert.AreEqual(expected, chooser.Select(i => (string)i.CommandParameter!).ToArray());
            Assert.IsTrue(chooser.Single(i => (string)i.CommandParameter! == "Scheme").IsChecked);
            Assert.IsFalse(chooser.Single(i => (string)i.CommandParameter! == "Started").IsChecked);
            Assert.IsFalse(chooser.Single(i => (string)i.CommandParameter! == "URL").IsEnabled);
            Assert.AreEqual("Reset columns", items.Last().Header);

            chooser.Single(i => (string)i.CommandParameter! == "Started").Command!.Execute("Started");
            Assert.IsTrue(Column(Grid(fx), "Started").IsVisible);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task ContextRequested_OnHeader_OpensChooser_ButRowKeepsItsOwnMenu()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.SeedSession(new SessionSnapshot
            {
                Id = 7,
                Method = "GET",
                StatusCode = 200,
                Host = "ctx.test",
                Url = "http://ctx.test/",
                Protocol = "HTTP/1.1",
            });
        });

        await fx.DispatchAsync(() =>
        {
            var grid = Grid(fx);
            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => h.IsVisible);
            var sessionMenu = grid.ContextMenu!;
            var onHeader = new ContextRequestedEventArgs { Source = header };
            header.RaiseEvent(onHeader);
            Assert.IsTrue(onHeader.Handled, "Header right-click must open the column chooser.");
            Assert.IsFalse(sessionMenu.IsOpen, "The session menu must not open over the header.");

            var row = grid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault();
            Assert.IsNotNull(row);
            var onRow = new ContextRequestedEventArgs { Source = row };
            row!.RaiseEvent(onRow);
            Assert.IsTrue(sessionMenu.IsOpen, "Rows keep the session context menu.");
            sessionMenu.Close();
        });
    }
}
