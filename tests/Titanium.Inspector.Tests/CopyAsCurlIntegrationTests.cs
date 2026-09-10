using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class CopyAsCurlIntegrationTests
{
    [TestMethod]
    public async Task ViewModel_CopyAsCurlAndFetch_UpdateStatusAndGenerate()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-copy-as-" + Guid.NewGuid().ToString("N") + ".json");
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

            Assert.IsFalse(vm.CanCopyAsCurl);
            Assert.IsFalse(vm.TryBuildCopyAsCurl(out _));

            var snap = new SessionSnapshot
            {
                Id = 1,
                Method = "POST",
                Url = "https://probe.example/copy",
                Host = "probe.example",
                RequestHeadersText = "Content-Type: application/json\n",
                RequestBodyText = "{\"ok\":true}",
            };
            vm.SeedSession(snap);
            vm.SelectedSession = snap;
            vm.SetSelectedSessions([snap]);

            Assert.IsTrue(vm.CanCopyAsCurl);
            Assert.IsTrue(vm.TryBuildCopyAsCurl(out var curl));
            StringAssert.Contains(curl, "curl 'https://probe.example/copy'");
            StringAssert.Contains(curl, "-X 'POST'");
            Assert.IsTrue(vm.TryBuildCopyAsFetch(out var fetch));
            StringAssert.Contains(fetch, "fetch(\"https://probe.example/copy\"");

            await ExecuteAsync(vm.CopyAsCurlCommand);
            StringAssert.Contains(vm.StatusText, "Copied as curl");

            await ExecuteAsync(vm.CopyAsFetchCommand);
            StringAssert.Contains(vm.StatusText, "Copied as fetch");

            var tunnel = new SessionSnapshot
            {
                Id = 2,
                Method = "CONNECT",
                Url = "https://opaque/",
                IsTunnel = true,
            };
            vm.SetSelectedSessions([tunnel]);
            Assert.IsFalse(vm.CanCopyAsCurl);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        await Task.Delay(150);
    }
}
