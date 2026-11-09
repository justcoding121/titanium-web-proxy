using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionRawHttp default body cap, InterceptionService
///     decrypt-bypass getters without proxy, SessionSearch count text, and VM clear-filters guard.
/// </summary>
[TestClass]
public class InspectorInterceptionRawHttpSoftCoverageWave19BehaviorTests
{
    [TestMethod]
    public void SessionRawHttp_NegativeMaxBody_UsesDefaultCap()
    {
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://cap-default.example/",
            ResponseBodyText = new string('z', 48),
            StatusCode = 200,
        };
        var result = SessionRawHttp.Write(snap, output, maxBodyBytes: 0);
        Assert.IsFalse(result.Truncated);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "GET /");
        StringAssert.Contains(text, new string('z', 48));
    }

    [TestMethod]
    public void InterceptionService_DecryptBypass_BeforeProxyStart_IsInert()
    {
        using var service = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            EnableDecryptFailureBypass = true,
        };
        service.ApplyDecryptFailureBypassSetting();
        Assert.AreEqual(0, service.GetDecryptFailureBypassEntries().Count);
        Assert.IsFalse(service.RemoveDecryptFailureBypass("absent.example"));
        service.ClearDecryptFailureBypass();
        Assert.IsFalse(service.ForceLearnDecryptBypass("absent.example"));
    }

    [TestMethod]
    public void SessionSearch_BuildCountText_AllVisible_NoFilter()
    {
        var now = DateTimeOffset.UtcNow;
        var text = SessionSearch.BuildSessionCountText(
            visibleCount: 4,
            totalCount: 4,
            searchQuery: null,
            retentionEvictedTotal: 0,
            oldestStartedUtc: now.AddMinutes(-5),
            nowUtc: now);
        StringAssert.Contains(text, "4");
        StringAssert.Contains(text, "Sessions", StringComparison.Ordinal);
        Assert.IsFalse(text.Contains("match filter", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MainWindowViewModel_ClearFilters_WithNoTokens_IsHarmless()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-wave19-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true });
            vm.SearchQuery = string.Empty;
            if (vm.ClearFiltersCommand.CanExecute(null))
            {
                vm.ClearFiltersCommand.Execute(null);
            }

            vm.SearchQuery = "host:wave19.example";
            vm.ClearFiltersCommand.Execute(null);
            Assert.IsTrue(string.IsNullOrWhiteSpace(vm.SearchQuery) || vm.SearchQuery == string.Empty);
            InvokeVoid(vm, "RefreshSessionCountText");
            Assert.IsFalse(string.IsNullOrWhiteSpace(vm.SessionCountText));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void InvokeVoid(MainWindowViewModel vm, string name, params object?[] args)
    {
        var method = typeof(MainWindowViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method!.Invoke(vm, args);
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
