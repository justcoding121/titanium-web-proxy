using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionGridColumnTests
{
    private static readonly string[] DefaultVisibleKeys =
        ["Id", "Method", "Status", "Host", "URL", "Protocol", "Duration", "TTFB", "Size", "Process"];

    [TestMethod]
    public void Catalog_DefaultVisibleColumns_MatchTodaysGrid()
    {
        var visible = SessionGridColumnCatalog.All
            .Where(c => c.DefaultVisible)
            .Select(c => c.Key)
            .ToArray();

        CollectionAssert.AreEqual(DefaultVisibleKeys, visible);
    }

    [TestMethod]
    public void Catalog_OptionalColumns_AreHiddenByDefaultAndComeLast()
    {
        var all = SessionGridColumnCatalog.All;
        CollectionAssert.AreEqual(
            new[] { "Started", "Scheme", "Content-Type" },
            all.Skip(DefaultVisibleKeys.Length).Select(c => c.Key).ToArray());
        Assert.IsTrue(all.Skip(DefaultVisibleKeys.Length).All(c => !c.DefaultVisible && c.CanHide));
    }

    [TestMethod]
    public void Catalog_KeysAndAutomationIds_AreUnique()
    {
        var all = SessionGridColumnCatalog.All;
        Assert.AreEqual(all.Count, all.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(all.Count, all.Select(c => c.MenuAutomationId).Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void IsVisible_WithoutOverrides_UsesDefaults()
    {
        Assert.IsTrue(SessionGridColumnCatalog.IsVisible("Host", null, true));
        Assert.IsFalse(SessionGridColumnCatalog.IsVisible("Started", null, true));
        Assert.IsFalse(SessionGridColumnCatalog.IsVisible("Content-Type", new Dictionary<string, bool>(), true));
    }

    [TestMethod]
    public void IsVisible_OverrideWinsOverDefault()
    {
        var overrides = new Dictionary<string, bool> { ["Protocol"] = false, ["Scheme"] = true };
        Assert.IsFalse(SessionGridColumnCatalog.IsVisible("Protocol", overrides, true));
        Assert.IsTrue(SessionGridColumnCatalog.IsVisible("Scheme", overrides, true));
    }

    [TestMethod]
    public void IsVisible_UrlAndHostAreAlwaysVisible()
    {
        // Host + URL identify the request: URL shows only the path (empty for CONNECT) because Host shows the host.
        var overrides = new Dictionary<string, bool> { ["URL"] = false, ["Host"] = false };
        Assert.IsTrue(SessionGridColumnCatalog.IsVisible("URL", overrides, true));
        Assert.IsTrue(SessionGridColumnCatalog.IsVisible("Host", overrides, true));
    }

    [TestMethod]
    public void IsVisible_ProcessIsHiddenWhenPlatformUnsupported()
    {
        Assert.IsFalse(SessionGridColumnCatalog.IsVisible("Process", null, platformAvailable: false));
        Assert.IsFalse(
            SessionGridColumnCatalog.IsVisible("Process", new Dictionary<string, bool> { ["Process"] = true }, false));
        Assert.IsTrue(SessionGridColumnCatalog.IsVisible("Process", null, platformAvailable: true));
    }

    [TestMethod]
    public void IsVisible_UnknownKeyIsNotVisible()
    {
        Assert.IsFalse(SessionGridColumnCatalog.IsVisible("Nope", null, true));
        Assert.IsFalse(SessionGridColumnCatalog.IsVisible(null, null, true));
    }

    [TestMethod]
    public void WithVisibility_StoresOnlyDifferencesFromDefault()
    {
        var shown = SessionGridColumnCatalog.WithVisibility(null, "Started", true);
        Assert.AreEqual(1, shown.Count);
        Assert.IsTrue(shown["Started"]);

        var back = SessionGridColumnCatalog.WithVisibility(shown, "Started", false);
        Assert.AreEqual(0, back.Count);

        var hiddenProtocol = SessionGridColumnCatalog.WithVisibility(back, "Protocol", false);
        Assert.IsFalse(hiddenProtocol["Protocol"]);
        Assert.AreEqual(0, SessionGridColumnCatalog.WithVisibility(hiddenProtocol, "Protocol", true).Count);
    }

    [TestMethod]
    public void WithVisibility_IgnoresUrlHostAndUnknownKeys_AndDoesNotMutateInput()
    {
        var input = new Dictionary<string, bool> { ["Scheme"] = true };
        Assert.AreEqual(1, SessionGridColumnCatalog.WithVisibility(input, "URL", false).Count);
        Assert.AreEqual(1, SessionGridColumnCatalog.WithVisibility(input, "Host", false).Count);
        Assert.AreEqual(1, SessionGridColumnCatalog.WithVisibility(input, "Nope", true).Count);

        SessionGridColumnCatalog.WithVisibility(input, "Protocol", false);
        Assert.AreEqual(1, input.Count);
    }

    [TestMethod]
    public void SessionGridColumnKeys_MatchHeaderKeys()
    {
        // XAML headers map to catalog keys through SessionGridLayout.GetColumnKey.
        Assert.AreEqual("Duration", SessionGridLayout.GetColumnKey("Duration (ms)"));
        Assert.AreEqual("TTFB", SessionGridLayout.GetColumnKey("Wait (ms)"));
        Assert.AreEqual("Content-Type", SessionGridLayout.GetColumnKey("Content-Type"));
        Assert.IsNotNull(SessionGridColumnCatalog.Find("TTFB"));
    }

    [TestMethod]
    public void Layout_LegacyJsonWithoutVisibility_LoadsWithDefaults()
    {
        using var temp = new TempSettings();
        var writer = new SettingsService(temp.Path);
        writer.Current.SessionGridLayout = new SessionGridLayoutDto
        {
            Columns = [new SessionGridColumnStateDto { Key = "Id", Width = 72, DisplayIndex = 0 }],
            SortColumnKey = "Id",
            SortDirection = ListSortDirection.Ascending,
        };
        writer.Save();

        // Rewrite the file the way an older build saved it: no ColumnVisibility property at all.
        var root = JsonNode.Parse(File.ReadAllText(temp.Path))!.AsObject();
        var layoutNode = root["sessionGridLayout"]!.AsObject();
        Assert.IsTrue(layoutNode.ContainsKey("columns"));
        layoutNode.Remove("columnVisibility");
        File.WriteAllText(temp.Path, root.ToJsonString());

        var layout = new SettingsService(temp.Path).Current.SessionGridLayout;
        Assert.IsNotNull(layout);
        Assert.IsNull(layout!.ColumnVisibility);
        Assert.IsTrue(SessionGridColumnCatalog.IsVisible("Host", layout.ColumnVisibility, true));
        Assert.IsFalse(SessionGridColumnCatalog.IsVisible("Started", layout.ColumnVisibility, true));
    }

    [TestMethod]
    public void Layout_ColumnVisibility_RoundTrips()
    {
        using var temp = new TempSettings();
        var svc = new SettingsService(temp.Path);
        svc.Current.SessionGridLayout = new SessionGridLayoutDto
        {
            SortColumnKey = "Id",
            SortDirection = ListSortDirection.Ascending,
            ColumnVisibility = new Dictionary<string, bool> { ["Started"] = true, ["Protocol"] = false },
        };
        svc.Save();

        var loaded = new SettingsService(temp.Path).Current.SessionGridLayout;
        Assert.IsNotNull(loaded?.ColumnVisibility);
        Assert.IsTrue(loaded!.ColumnVisibility!["Started"]);
        Assert.IsFalse(loaded.ColumnVisibility["Protocol"]);
    }

    [TestMethod]
    public void ViewModel_SetGridColumnVisible_PersistsImmediatelyAndRaisesChange()
    {
        using var fixture = new VmFixture();
        var raised = new List<string?>();
        fixture.Vm.GridColumnsChanged += raised.Add;

        Assert.IsFalse(fixture.Vm.IsGridColumnVisible("Started"));
        Assert.IsTrue(fixture.Vm.SetGridColumnVisible("Started", true));
        Assert.IsTrue(fixture.Vm.IsGridColumnVisible("Started"));
        CollectionAssert.AreEqual(new[] { "Started" }, raised);

        var reloaded = new SettingsService(fixture.Path).Current.SessionGridLayout;
        Assert.IsTrue(reloaded?.ColumnVisibility?["Started"]);
    }

    [TestMethod]
    public void ViewModel_SetGridColumnVisible_RejectsUrlAndUnknown()
    {
        using var fixture = new VmFixture();
        var raised = 0;
        fixture.Vm.GridColumnsChanged += _ => raised++;

        Assert.IsFalse(fixture.Vm.SetGridColumnVisible("URL", false));
        Assert.IsFalse(fixture.Vm.SetGridColumnVisible("Host", false));
        Assert.IsFalse(fixture.Vm.SetGridColumnVisible("Nope", true));
        Assert.IsTrue(fixture.Vm.IsGridColumnVisible("URL"));
        Assert.IsTrue(fixture.Vm.IsGridColumnVisible("Host"));
        Assert.AreEqual(0, raised);
    }

    [TestMethod]
    public void ViewModel_ToggleCommand_FlipsVisibilityByParameter()
    {
        using var fixture = new VmFixture();
        var command = fixture.Vm.ToggleGridColumnCommand;

        Assert.IsTrue(command.CanExecute("Scheme"));
        command.Execute("Scheme");
        Assert.IsTrue(fixture.Vm.IsGridColumnVisible("Scheme"));
        command.Execute("Scheme");
        Assert.IsFalse(fixture.Vm.IsGridColumnVisible("Scheme"));
        Assert.IsNull(fixture.Settings.Current.SessionGridLayout?.ColumnVisibility);

        Assert.IsFalse(command.CanExecute("URL"));
        Assert.IsFalse(command.CanExecute("Host"));
        Assert.IsFalse(command.CanExecute(null));
    }

    [TestMethod]
    public void ViewModel_ResetGridColumns_ClearsLayoutAndRaisesNull()
    {
        using var fixture = new VmFixture();
        fixture.Vm.SetGridColumnVisible("Started", true);
        fixture.Vm.PersistSessionGridLayout(new SessionGridLayoutDto
        {
            Columns = [new SessionGridColumnStateDto { Key = "Id", Width = 300, DisplayIndex = 3 }],
        });
        var raised = new List<string?>();
        fixture.Vm.GridColumnsChanged += raised.Add;

        fixture.Vm.ResetGridColumns();

        Assert.IsNull(fixture.Settings.Current.SessionGridLayout);
        Assert.IsFalse(fixture.Vm.IsGridColumnVisible("Started"));
        CollectionAssert.AreEqual(new string?[] { null }, raised);
    }

    [TestMethod]
    public void ViewModel_PersistSessionGridLayout_KeepsColumnVisibility()
    {
        using var fixture = new VmFixture();
        fixture.Vm.SetGridColumnVisible("Content-Type", true);

        // The window captures widths/order/sort only; it never knows about visibility.
        fixture.Vm.PersistSessionGridLayout(new SessionGridLayoutDto
        {
            Columns = [new SessionGridColumnStateDto { Key = "Id", Width = 80, DisplayIndex = 0 }],
        });

        Assert.IsTrue(fixture.Vm.IsGridColumnVisible("Content-Type"));
        Assert.AreEqual(80, fixture.Settings.Current.SessionGridLayout!.Columns[0].Width);
    }

    [TestMethod]
    public void ViewModel_FactoryReset_ForgetsColumnVisibility()
    {
        using var fixture = new VmFixture();
        fixture.Vm.SetGridColumnVisible("Started", true);

        fixture.Settings.ResetToFactoryDefaults();

        Assert.IsNull(fixture.Settings.Current.SessionGridLayout);
        Assert.IsFalse(fixture.Vm.IsGridColumnVisible("Started"));
    }

    [TestMethod]
    public void FormatStarted_TodayHasTimeOnly_OtherDayHasDate()
    {
        var now = new DateTimeOffset(2026, 10, 7, 14, 30, 0, TimeSpan.Zero);
        var sameDay = now.AddMinutes(-5);
        var earlier = now.AddDays(-2);

        var expectedToday = sameDay.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        Assert.AreEqual(expectedToday, SessionDisplayFormat.FormatStarted(sameDay, now));

        var other = SessionDisplayFormat.FormatStarted(earlier, now);
        Assert.IsTrue(other.Contains(earlier.ToLocalTime().Day.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal));
        Assert.IsTrue(other.Length > expectedToday.Length);
    }

    [TestMethod]
    public void FormatContentType_DropsParameters()
    {
        Assert.AreEqual("application/json", SessionDisplayFormat.FormatContentType("application/json; charset=utf-8"));
        Assert.AreEqual("text/html", SessionDisplayFormat.FormatContentType("  text/html ;q=1"));
        Assert.AreEqual("image/png", SessionDisplayFormat.FormatContentType("image/png"));
        Assert.AreEqual("", SessionDisplayFormat.FormatContentType(null));
        Assert.AreEqual("", SessionDisplayFormat.FormatContentType("  "));
    }

    [TestMethod]
    public void GetScheme_ParsesBeforeSeparator()
    {
        Assert.AreEqual("https", SessionDisplayFormat.GetScheme("https://example.com/a?b=1"));
        Assert.AreEqual("wss", SessionDisplayFormat.GetScheme("WSS://example.com/socket"));
        Assert.AreEqual("", SessionDisplayFormat.GetScheme("example.com:443"));
        Assert.AreEqual("", SessionDisplayFormat.GetScheme("/relative/path"));
        Assert.AreEqual("", SessionDisplayFormat.GetScheme("://nope"));
        Assert.AreEqual("", SessionDisplayFormat.GetScheme("not a scheme://x"));
        Assert.AreEqual("", SessionDisplayFormat.GetScheme(null));
        Assert.AreEqual("", SessionDisplayFormat.GetScheme(""));
    }

    [TestMethod]
    public void SessionSnapshot_ContentTypeDisplay_RaisesWhenContentTypeChanges()
    {
        var snap = new SessionSnapshot { Url = "https://example.com/" };
        var changed = new List<string?>();
        snap.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        snap.ContentType = "text/html; charset=utf-8";

        CollectionAssert.Contains(changed, nameof(SessionSnapshot.ContentTypeDisplay));
        Assert.AreEqual("text/html", snap.ContentTypeDisplay);
        Assert.AreEqual("https", snap.Scheme);
        Assert.IsFalse(string.IsNullOrEmpty(snap.StartedDisplay));
    }

    private sealed class TempSettings : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "twp-grid-cols-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // ignore
            }
        }
    }

    private sealed class VmFixture : IDisposable
    {
        private readonly InterceptionService _interception;

        public VmFixture()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "twp-grid-vm-" + Guid.NewGuid().ToString("N") + ".json");
            Settings = new SettingsService(Path);
            var registry = new SessionRegistry();
            _interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            Vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(Settings),
                Settings,
                _interception);
        }

        public string Path { get; }

        public SettingsService Settings { get; }

        public MainWindowViewModel Vm { get; }

        public void Dispose()
        {
            _interception.Dispose();
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // ignore
            }
        }
    }
}
