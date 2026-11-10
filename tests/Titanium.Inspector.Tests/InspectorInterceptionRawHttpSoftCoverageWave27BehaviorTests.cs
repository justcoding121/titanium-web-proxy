using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: invalid status chrome, combined class tokens,
///     InterceptionService decrypt prefs, SessionRawHttp verbs, and indented HAR export.
/// </summary>
[TestClass]
public class InspectorInterceptionRawHttpSoftCoverageWave27BehaviorTests
{
    [TestMethod]
    public void MainWindowViewModel_InvalidStatusFreeText_SetsFilterError()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-wave27-" + Guid.NewGuid().ToString("N") + ".json");
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

            Assert.IsFalse(vm.HasStatusFilterError);
            vm.StatusFreeText = "nope,,,bad";
            Assert.IsTrue(vm.HasStatusFilterError);
            Assert.IsFalse(string.IsNullOrEmpty(vm.StatusFilterError));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void MainWindowViewModel_CombinedStatusClasses_WritesJoinedToken()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-wave27b-" + Guid.NewGuid().ToString("N") + ".json");
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

            vm.StatusClass4xx = true;
            vm.StatusClass5xx = true;
            StringAssert.Contains(vm.SearchQuery, "4xx");
            StringAssert.Contains(vm.SearchQuery, "5xx");
            Assert.IsTrue(vm.StatusFilterActive);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void InterceptionService_DecryptHostLists_BeforeStart_NoThrow()
    {
        using var service = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            DecryptHttps = true,
        };
        service.DecryptSkipHosts = ["never.example"];
        service.DecryptOnlyHosts = ["only.example"];
        service.ApplyDecryptFailureBypassSetting();
        CollectionAssert.Contains(service.DecryptSkipHosts.ToList(), "never.example");
        CollectionAssert.Contains(service.DecryptOnlyHosts.ToList(), "only.example");
    }

    [TestMethod]
    public void SessionRawHttp_DeleteAndOptions_WritesRequestLines()
    {
        foreach (var method in new[] { "DELETE", "OPTIONS" })
        {
            using var output = new MemoryStream();
            var snap = new SessionSnapshot
            {
                Method = method,
                Url = $"https://verbs.example/{method.ToLowerInvariant()}",
                Host = "verbs.example",
                StatusCode = 204,
            };
            SessionRawHttp.Write(snap, output);
            var text = Encoding.UTF8.GetString(output.ToArray());
            StringAssert.StartsWith(text, $"{method} ");
            StringAssert.Contains(text, "204");
        }
    }

    [TestMethod]
    public void SessionArchive_BuildHarDocumentJson_Indented_IncludesEntries()
    {
        var snap = new SessionSnapshot
        {
            Id = 7,
            Method = "GET",
            Url = "https://har-wave27.example/",
            Host = "har-wave27.example",
            StatusCode = 200,
        };
        var json = SessionArchive.BuildHarDocumentJson([snap], indented: true);
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual(1, doc.RootElement.GetProperty("log").GetProperty("entries").GetArrayLength());
        StringAssert.Contains(json, "har-wave27.example");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
