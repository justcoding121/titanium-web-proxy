using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionGridLayoutTests
{
    [TestMethod]
    public void GetColumnKey_UsesStringOrTextBlockTag()
    {
        Assert.AreEqual("Id", SessionGridLayout.GetColumnKey("Id"));
        Assert.AreEqual("Duration", SessionGridLayout.GetColumnKey("Duration (ms)"));
        Assert.AreEqual("TTFB", SessionGridLayout.GetColumnKey("TTFB (ms)"));
        Assert.AreEqual(
            "Duration",
            SessionGridLayout.GetColumnKey(new Avalonia.Controls.TextBlock { Text = "Duration (ms)", Tag = "Duration" }));
        Assert.AreEqual(
            "TTFB",
            SessionGridLayout.GetColumnKey(new Avalonia.Controls.TextBlock { Text = "TTFB (ms)" }));
        Assert.IsNull(SessionGridLayout.GetColumnKey(42));
    }

    [TestMethod]
    public void ResolveSort_WhenNoLayout_UsesIdAscending()
    {
        SessionGridLayout.ResolveSort(null, out var key, out var direction);
        Assert.AreEqual(SessionGridLayout.DefaultSortColumnKey, key);
        Assert.AreEqual(ListSortDirection.Ascending, direction);
    }

    [TestMethod]
    public void ResolveSort_WhenSortIncomplete_UsesIdAscending()
    {
        SessionGridLayout.ResolveSort(
            new SessionGridLayoutDto { SortColumnKey = "Host" },
            out var key,
            out var direction);
        Assert.AreEqual("Id", key);
        Assert.AreEqual(ListSortDirection.Ascending, direction);
    }

    [TestMethod]
    public void ResolveSort_WhenPersisted_UsesSavedSort()
    {
        SessionGridLayout.ResolveSort(
            new SessionGridLayoutDto
            {
                SortColumnKey = "Id",
                SortDirection = ListSortDirection.Descending,
            },
            out var key,
            out var direction);
        Assert.AreEqual("Id", key);
        Assert.AreEqual(ListSortDirection.Descending, direction);
    }

    [TestMethod]
    public void IndexByKey_SkipsEmptyKeys_AndLastWins()
    {
        var map = SessionGridLayout.IndexByKey(
        [
            new SessionGridColumnStateDto { Key = "", Width = 1 },
            new SessionGridColumnStateDto { Key = "URL", Width = 100, DisplayIndex = 0 },
            new SessionGridColumnStateDto { Key = "URL", Width = 280, DisplayIndex = 4 },
        ]);

        Assert.AreEqual(1, map.Count);
        Assert.AreEqual(280, map["URL"].Width);
        Assert.AreEqual(4, map["URL"].DisplayIndex);
    }

    [TestMethod]
    public void ResolvePersistableWidth_PrefersActual()
    {
        Assert.AreEqual(120, SessionGridLayout.ResolvePersistableWidth(120, true, 60));
        Assert.AreEqual(60, SessionGridLayout.ResolvePersistableWidth(0, true, 60));
        Assert.AreEqual(0, SessionGridLayout.ResolvePersistableWidth(0, false, 60));
    }

    [TestMethod]
    public void SettingsService_RoundTripsSessionGridLayout()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "twp-session-grid-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new SettingsService(path);
            svc.Current.SessionGridLayout = new SessionGridLayoutDto
            {
                SortColumnKey = "Id",
                SortDirection = ListSortDirection.Descending,
                Columns =
                [
                    new SessionGridColumnStateDto { Key = "Id", Width = 72, DisplayIndex = 0 },
                    new SessionGridColumnStateDto { Key = "URL", Width = 400, DisplayIndex = 1 },
                ],
            };
            svc.Save();

            var loaded = new SettingsService(path).Current.SessionGridLayout;
            Assert.IsNotNull(loaded);
            Assert.AreEqual("Id", loaded!.SortColumnKey);
            Assert.AreEqual(ListSortDirection.Descending, loaded.SortDirection);
            Assert.AreEqual(2, loaded.Columns.Count);
            Assert.AreEqual(400, loaded.Columns[1].Width);
            Assert.AreEqual(1, loaded.Columns[1].DisplayIndex);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
