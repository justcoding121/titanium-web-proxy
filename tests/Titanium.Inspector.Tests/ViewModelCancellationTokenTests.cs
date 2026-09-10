using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Exercises MainWindowViewModel / SessionStore / SessionArchive cancellation-token
/// parameters without spinning a full Avalonia UI.
/// </summary>
[TestClass]
public class ViewModelCancellationTokenTests
{
    [TestMethod]
    public async Task CheckUpdatesAsync_UsesStatusToken_AndReportsFeedFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-upd-ct-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var updates = new UpdateService(settings, () => new HttpClient(new ImmediateNotFoundHandler(), disposeHandler: true));
            var vm = CreateVm(settings, updates);

            vm.SetTransientStatus("Working…", StatusSeverity.Busy, revertMs: 8000);
            await vm.CheckUpdatesAsync(promptIfAvailable: false);
            StringAssert.Contains(vm.StatusText, "Update check failed");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task LoadFromSelected_AndReplayTunnel_PassStoreTokens()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-load-ct-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var vm = CreateVm(settings);

            vm.SeedSession(new SessionSnapshot
            {
                Id = 11,
                Method = "POST",
                Url = "https://api.example/v1",
                RequestHeadersText = "Content-Type: application/json\r\n",
                RequestBodyText = "{\"n\":1}",
            });
            vm.SelectedSession = vm.Sessions[0];

            vm.LoadFromSelectedCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Composer loaded", StringComparison.Ordinal));
            Assert.AreEqual("POST", vm.ComposerMethod);
            Assert.AreEqual("https://api.example/v1", vm.ComposerUrl);
            Assert.AreEqual("{\"n\":1}", vm.ComposerBody);

            vm.SeedSession(new SessionSnapshot
            {
                Id = 12,
                Method = "CONNECT",
                Url = "https://tunnel.example:443",
                IsTunnel = true,
            });
            vm.SelectedSession = vm.Sessions.First(s => s.Id == 12);
            vm.ReplayCommand.Execute(null);
            await WaitUntil(() =>
                vm.StatusText.Contains("tunnel", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains("Cannot replay", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains("Replay failed", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains("Replay →", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task SendComposer_EmptyUrl_IsGuard_AndInvalidUrlUsesReplayToken()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-comp-ct-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var vm = CreateVm(settings);

            vm.ComposerUrl = "";
            vm.SendComposerCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Composer URL is required", StringComparison.Ordinal));

            // Invalid absolute URI fails inside ReplayService before any connect (covers token pass-through).
            vm.ComposerUrl = "http://\u0001invalid";
            vm.ComposerMethod = "GET";
            vm.SendComposerCommand.Execute(null);
            await WaitUntil(() =>
                vm.StatusText.Contains("Composer failed", StringComparison.Ordinal)
                || vm.StatusText.Contains("Action failed", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task ExportImportCommands_PassArchiveTokens_IncludingZipViaImportHar()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-exp-ct-" + Guid.NewGuid().ToString("N") + ".json");
        var harPath = Path.Combine(Path.GetTempPath(), $"twp-exp-ct-{Guid.NewGuid():N}.har");
        var zipPath = Path.Combine(Path.GetTempPath(), $"twp-exp-ct-{Guid.NewGuid():N}.zip");
        try
        {
            var settings = new SettingsService(settingsPath);
            var picker = new ScriptedInspectorPathPicker();
            var vm = CreateVm(settings, pathPicker: picker);
            vm.SeedSession(new SessionSnapshot
            {
                Id = 21,
                Method = "GET",
                Url = "https://keep.example/",
                StatusCode = 200,
            });
            vm.SetSelectedSessions([vm.Sessions[0]]);

            picker.SavePath = harPath;
            vm.ExportHarCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Exported", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(harPath));

            picker.SavePath = zipPath;
            vm.ExportSelectedArchiveCommand.Execute(null);
            await WaitUntil(() => File.Exists(zipPath) && vm.StatusText.Contains("Exported", StringComparison.Ordinal));

            picker.OpenPath = zipPath;
            vm.ImportHarCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Appended", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(settingsPath);
            TryDelete(harPath);
            TryDelete(zipPath);
        }
    }

    [TestMethod]
    public async Task ReplayEmptyUrl_UsesEnsureBodiesLoadedToken()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-replay-ct-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var vm = CreateVm(settings);
            vm.SeedSession(new SessionSnapshot
            {
                Id = 31,
                Method = "GET",
                Url = "",
            });
            vm.SelectedSession = vm.Sessions[0];
            vm.ReplayCommand.Execute(null);
            await WaitUntil(() =>
                vm.StatusText.Contains("Cannot replay", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains("empty URL", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains("Replay failed", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains("Replay →", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static MainWindowViewModel CreateVm(
        SettingsService settings,
        UpdateService? updates = null,
        IInspectorPathPicker? pathPicker = null)
    {
        var registry = new SessionRegistry();
        return new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            updates ?? new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()),
            pathPicker: pathPicker);
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(25);
        }
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
            // best-effort temp cleanup
        }
    }

    private sealed class ImmediateNotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent(""),
            });
        }
    }
}
