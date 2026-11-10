using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionStore ApplyOptions retention,
///     SessionArchive HAR JSON, InterceptionService HTTP/3 prefs, SessionSearch url/body,
///     and MainWindowViewModel 1xx status-class token.
/// </summary>
[TestClass]
public class InspectorInterceptionRawHttpSoftCoverageWave26BehaviorTests
{
    private static readonly List<SessionSnapshot> SearchSample =
    [
        new()
        {
            Id = 1,
            Method = "GET",
            Url = "https://needle.example/path",
            Host = "needle.example",
            StatusCode = 100,
        },
        new()
        {
            Id = 2,
            Method = "POST",
            Url = "https://other.example/submit",
            Host = "other.example",
            RequestBodyText = "alpha-body",
            StatusCode = 201,
        },
    ];

    [TestMethod]
    public void SessionStore_ApplyOptions_TrimsInMemoryCount()
    {
        var store = new SessionStore(new SessionStoreOptions { SpillBodiesToDisk = false, MaxSessionsInMemory = 20 });
        for (var i = 1; i <= 6; i++)
        {
            store.Add(new SessionSnapshot
            {
                Id = i,
                Method = "GET",
                Url = $"https://store-wave26.example/{i}",
                StatusCode = 200,
            });
        }

        Assert.AreEqual(6, store.Count);
        store.ApplyOptions(new SessionStoreOptions { SpillBodiesToDisk = false, MaxSessionsInMemory = 2 });
        Assert.AreEqual(2, store.Count);
        Assert.IsNotNull(store.TryGet(6));
        Assert.IsNull(store.TryGet(1));
        store.Dispose();
    }

    [TestMethod]
    public void SessionArchive_BuildHarDocumentJson_RoundTripsCoreFields()
    {
        var snap = new SessionSnapshot
        {
            Id = 12,
            Method = "PUT",
            Url = "https://har-wave26.example/item",
            Host = "har-wave26.example",
            StatusCode = 204,
            RequestBodyText = "payload",
        };
        var json = SessionArchive.BuildHarDocumentJson([snap], indented: false);
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("log").GetProperty("entries")[0];
        Assert.AreEqual("PUT", entry.GetProperty("request").GetProperty("method").GetString());
        Assert.AreEqual(204, entry.GetProperty("response").GetProperty("status").GetInt32());
        StringAssert.Contains(json, "har-wave26.example");
    }

    [TestMethod]
    public void SessionSearch_UrlAndBody_Filters()
    {
        Assert.AreEqual(1, SessionSearch.Filter(SearchSample, "url:needle").Count());
        Assert.AreEqual(1, SessionSearch.Filter(
            SearchSample,
            "body:alpha",
            (s, q) => (s.RequestBodyText ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)).Count());
    }

    [TestMethod]
    public void InterceptionService_PreferHttp3BeforeStart_NoThrow()
    {
        using var service = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            PreferHttp2 = false,
            PreferHttp3 = true,
        };
        service.ApplyHttpProtocols();
        Assert.IsTrue(service.PreferHttp3);
    }

    [TestMethod]
    public void MainWindowViewModel_StatusFreeText_WritesExactCodes()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-wave26-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Save();
            var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true });

            vm.StatusFreeText = "100,101";
            StringAssert.Contains(vm.SearchQuery, "status:100");
            StringAssert.Contains(vm.SearchQuery, "101");
            vm.StatusClass3xx = true;
            StringAssert.Contains(vm.SearchQuery, "status:3xx");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void SessionRawHttp_Status100_ContinueReasonPhrase()
    {
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://continue.example/",
            StatusCode = 100,
        };
        SessionRawHttp.Write(snap, output);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "HTTP/1.1 100\r\n");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
