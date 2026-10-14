using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Views;

namespace Titanium.E2E.Tests.UiHeadless;

[TestClass]
public class FilterStripHeadlessTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task MethodAndStatusButtons_KeepTheirWidthWhenTheCountChanges()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(visualSkia: true);
        await fx.DispatchAsync(() =>
        {
            AssertWidthsStable(fx, "en");
            AssertActivePill(fx);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task FinnishLabels_DoNotMoveWhenTheCountChanges()
    {
        LanguageService.PinForTests("fi");
        try
        {
            await using var fx = new InspectorHeadlessFixture();
            await fx.StartAsync();
            await fx.DispatchAsync(() => AssertWidthsStable(fx, "fi"));
        }
        finally
        {
            LanguageService.PinForTests(LanguageService.English);
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task SearchBoxes_WrapFromTheTop_AndEnterStaysOneLine()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            Assert.IsTrue(fx.Robot.TryFind<TextBox>("SearchBox", out var search) && search is not null);
            Assert.AreEqual(TextWrapping.Wrap, search!.TextWrapping);
            Assert.AreEqual(VerticalAlignment.Top, search.VerticalContentAlignment);
            Assert.IsFalse(search.AcceptsReturn);

            search.ApplyTemplate();
            Dispatcher.UIThread.RunJobs();
            var watermark = search.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(block => block.Name == "PART_Watermark");
            Assert.IsNotNull(watermark, "Search watermark was not created.");
            Assert.AreEqual(VerticalAlignment.Top, watermark!.VerticalAlignment);

            var query = string.Join(' ', Enumerable.Repeat("abcdefghij", 20));
            Assert.IsTrue(query.Length >= 200);
            search.Text = query;
            var dialog = new SearchFiltersWindow("");
            dialog.Width = 560;
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            var remainder = FindById<TextBox>(dialog, "SearchFiltersRemainder");
            Assert.IsNotNull(remainder, "Advanced search box was not created.");
            Assert.AreEqual(TextWrapping.Wrap, remainder.TextWrapping);
            Assert.AreEqual(VerticalAlignment.Top, remainder.VerticalContentAlignment);
            remainder.Width = search.Bounds.Width;
            remainder.Text = query;
            Dispatcher.UIThread.RunJobs();

            var searchLines = LineCount(search);
            var dialogLines = LineCount(remainder);
            Assert.IsTrue(searchLines > 1, $"Search box did not wrap ({searchLines} lines).");
            Assert.AreEqual(searchLines, dialogLines);

            var resource = SearchQueryHeight();
            Assert.IsTrue(fx.Robot.TryFind<Border>("ToolbarFilterGroup", out var filters) && filters is not null);
            Assert.AreEqual(
                resource,
                filters!.Bounds.Height,
                1.0,
                $"One-row Filters border is {filters.Bounds.Height}. Set SearchQueryBoxHeight to that.");
            Assert.IsTrue(search.Bounds.Height + 0.5 >= remainder.Bounds.Height);
            Assert.AreEqual(filters.Bounds.Height, search.Bounds.Height, 1.0);

            var before = fx.ViewModel.SearchQuery;
            search.Focus();
            fx.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(before, fx.ViewModel.SearchQuery);
            Assert.IsFalse(search.Text!.Contains('\n'));

            search.Text = "one\r\ntwo\nthree";
            Assert.AreEqual("one two three", search.Text);
            Assert.AreEqual("one two three", fx.ViewModel.SearchQuery);

            remainder.Text = query;
            remainder.Focus();
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(dialog.IsVisible);
            Assert.AreEqual(query, dialog.AppliedQuery);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task OnlyLink_AppearsWhenTheRowIsFocused()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            Assert.IsTrue(fx.Robot.TryFind<DropDownButton>("MethodFilterButton", out var button) && button is not null);
            button!.Flyout?.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            var only = FindById<Button>(fx.Window, "MethodFilterOnly_GET");
            Assert.IsNotNull(only, "Only link was not in the open flyout.");
            Assert.AreEqual(0, only!.Opacity, 0.01);
            only.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(1, only.Opacity, 0.01);
        });
    }

    private static void AssertWidthsStable(InspectorHeadlessFixture fx, string culture)
    {
        Assert.IsTrue(fx.Robot.TryFind<DropDownButton>("MethodFilterButton", out var method) && method is not null);
        Assert.IsTrue(fx.Robot.TryFind<DropDownButton>("StatusFilterButton", out var status) && status is not null);
        Assert.IsTrue(fx.Robot.TryFind<CheckBox>("HideImagesFilterCheck", out var images) && images is not null);
        Layout(fx);
        var methodWidth = method!.Bounds.Width;
        var statusWidth = status!.Bounds.Width;
        var imagesX = images!.Bounds.X;
        Assert.IsTrue(methodWidth > 0, culture);

        fx.ViewModel.SearchQuery = "-method:GET";
        Layout(fx);
        Assert.AreEqual(methodWidth, method.Bounds.Width, 0.5, culture);
        Assert.AreEqual(statusWidth, status.Bounds.Width, 0.5, culture);
        Assert.AreEqual(imagesX, images.Bounds.X, 0.5, culture);

        fx.ViewModel.SearchQuery = "-method:GET,POST,PUT,PATCH,DELETE,HEAD,OPTIONS,CONNECT,PROPFIND,TRACE status:2xx,3xx,4xx,5xx,304,400,401,404,500,503";
        Layout(fx);
        Assert.AreEqual("9+", fx.ViewModel.MethodFilterBadge, culture);
        Assert.AreEqual("9+", fx.ViewModel.StatusFilterBadge, culture);
        Assert.AreEqual(methodWidth, method.Bounds.Width, 0.5, culture);
        Assert.AreEqual(statusWidth, status.Bounds.Width, 0.5, culture);
        Assert.AreEqual(imagesX, images.Bounds.X, 0.5, culture);

        for (var i = 0; i < 12; i++)
        {
            fx.ViewModel.ConnectMethodRow!.IsChecked = i % 2 == 0;
        }

        Layout(fx);
        Assert.AreEqual(methodWidth, method.Bounds.Width, 0.5, culture);
        Assert.AreEqual(imagesX, images.Bounds.X, 0.5, culture);
    }

    private static void AssertActivePill(InspectorHeadlessFixture fx)
    {
        fx.ViewModel.SearchQuery = "-method:GET,CONNECT status:2xx,304";
        Layout(fx);
        Assert.IsTrue(fx.Robot.TryFind<DropDownButton>("MethodFilterButton", out var method) && method is not null);
        Assert.IsTrue(method!.Classes.Contains("active"), "classes: " + string.Join(",", method.Classes));
        var badge = method.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("filterBadge"));
        Assert.IsNotNull(badge, "Count pill was not created.");
        Assert.AreEqual(1, badge!.Opacity, 0.01, "pill opacity");
        Assert.AreEqual("2", (badge.Child as TextBlock)?.Text);
        var presenter = method.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(item => item.Name == "PART_ContentPresenter");
        Assert.IsInstanceOfType<ISolidColorBrush>(presenter?.BorderBrush);
        var outline = ((ISolidColorBrush)presenter!.BorderBrush!).Color;
        Assert.IsTrue(outline.B > outline.R, $"Outline was #{outline.R:X2}{outline.G:X2}{outline.B:X2}.");
    }

    private static void Layout(InspectorHeadlessFixture fx)
    {
        fx.Window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    private static int LineCount(TextBox box)
    {
        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        Assert.IsNotNull(presenter, "Text presenter was not created.");
        return presenter!.TextLayout.TextLines.Count;
    }

    private static double SearchQueryHeight()
    {
        var value = Application.Current!.FindResource("SearchQueryBoxHeight");
        Assert.IsInstanceOfType<double>(value);
        return (double)value!;
    }

    private static T? FindById<T>(Visual root, string automationId) where T : Control
    {
        foreach (var control in root.GetVisualDescendants().OfType<T>())
        {
            if (AutomationProperties.GetAutomationId(control) == automationId)
            {
                return control;
            }
        }

        return null;
    }
}
