using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.E2E.Tests;

/// <summary>
/// Avalonia Headless smoke without Desktop/UsePlatformDetect (those hang CI).
/// Full visual-tree headless is exercised locally via <c>tools/InspectorUiDocker</c>;
/// this portable test validates tab-index / session selection wiring used by MainWindow bindings.
/// </summary>
[TestClass]
public class InspectorAvaloniaHeadlessE2ETests
{
    [TestMethod]
    [TestCategory("E2E-UI")]
    public void MainWindowBindings_SelectSession_AndCycleTabs()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-avalonia-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry();
            var buffer = new SessionStreamBuffer(registry);
            var updates = new UpdateService(settings);
            var interception = new InterceptionService(new RecordingSystemProxyController());
            var vm = new MainWindowViewModel(buffer, registry, updates, settings, interception);
            vm.BindPort = CliProcessHarness.GetFreePort();

            vm.Sessions.Add(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                StatusCode = 200,
                Host = "127.0.0.1",
                Url = "http://127.0.0.1/headless",
                Protocol = "HTTP/1.1",
            });
            vm.SelectedSession = vm.Sessions[0];
            Assert.IsNotNull(vm.SelectedSession);
            Assert.IsFalse(vm.ShowWsFramesTab);
            Assert.AreEqual(0, vm.SelectedOuterPaneIndex);

            for (var i = 0; i < 8; i++)
            {
                vm.SelectedDetailTabIndex = i;
                Assert.AreEqual(i, vm.SelectedDetailTabIndex);
            }

            vm.OpenToolsScriptsCommand.Execute(null);
            Assert.IsTrue(vm.ShowSessionDetails);
            Assert.AreEqual(1, vm.SelectedOuterPaneIndex);
            Assert.AreEqual(3, vm.SelectedToolsTabIndex);
            Assert.AreEqual(7, vm.SelectedDetailTabIndex);

            vm.SelectedSession = new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "wss://example.com/ws",
                IsWebSocket = true,
            };
            Assert.IsTrue(vm.ShowWsFramesTab);

            // Document Docker path for Linux visual-tree runs.
            Assert.IsTrue(File.Exists(Path.Combine(
                CliProcessHarness.FindRepoRoot(), "tools", "InspectorUiDocker", "Dockerfile")));
        }
        finally
        {
            try { File.Delete(settingsPath); } catch { /* ignore */ }
        }
    }
}
