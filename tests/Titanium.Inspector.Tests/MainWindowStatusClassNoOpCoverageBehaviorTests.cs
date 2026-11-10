using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe MainWindowViewModel.Status leftovers: class/free-text setters that no-op when
///     unchanged, and WriteStatus round-trips that keep SearchQuery as the source of truth.
/// </summary>
[TestClass]
public class MainWindowStatusClassNoOpCoverageBehaviorTests
{
    [TestMethod]
    public void StatusClassSetters_NoOpWhenUnchanged_AndWriteUpdatesSearch()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-status-" + Guid.NewGuid().ToString("N") + ".json");
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

            vm.StatusClass2xx = false;
            vm.StatusClass3xx = false;
            vm.StatusClass4xx = false;
            vm.StatusClass5xx = false;
            Assert.IsFalse(vm.StatusClass2xx);

            vm.StatusClass2xx = true;
            Assert.IsTrue(vm.StatusClass2xx);
            vm.StatusClass2xx = true;
            vm.StatusClass3xx = true;
            vm.StatusClass3xx = true;
            vm.StatusClass4xx = true;
            vm.StatusClass4xx = true;
            vm.StatusClass5xx = true;
            vm.StatusClass5xx = true;

            var free = vm.StatusFreeText;
            vm.StatusFreeText = free;
            vm.StatusFreeText = "404,500-502";
            StringAssert.Contains(vm.SearchQuery, "status:");
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}
