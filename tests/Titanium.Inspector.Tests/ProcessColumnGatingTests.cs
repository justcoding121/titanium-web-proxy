using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy;

namespace Titanium.Inspector.Tests;

[TestClass]
public class ProcessColumnGatingTests
{
    [TestMethod]
    public void ShowProcessColumn_TracksClientProcessIdSupport()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-proc-col-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            Assert.AreEqual(ClientProcessId.IsSupported, vm.ShowProcessColumn);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }
    }

    [TestMethod]
    public void CanFilterByProcess_RequiresShowProcessColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-proc-filter-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            var snap = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/",
                Host = "example.com",
                ProcessName = "chrome",
                ProcessId = 99,
            };
            vm.SeedSession(snap);
            vm.SelectedSession = snap;
            vm.SetSelectedSessions([snap]);

            Assert.AreEqual(vm.ShowProcessColumn, vm.CanFilterByProcess);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }
    }
}
