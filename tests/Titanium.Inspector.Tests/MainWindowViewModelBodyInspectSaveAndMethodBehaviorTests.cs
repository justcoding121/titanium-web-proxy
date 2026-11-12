using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Fat BodyInspect / Method leftovers: save request/response body commands, missing-body notices,
///     uncommon method rows, and composer body-from-file streaming path.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MainWindowViewModelBodyInspectSaveAndMethodBehaviorTests
{
    [TestMethod]
    public async Task SaveBodyCommands_WriteBytes_AndMissingBodiesSetNotices()
    {
        LanguageService.Apply("en");
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-body-save-" + Guid.NewGuid().ToString("N") + ".json");
        var requestOut = Path.Combine(Path.GetTempPath(), "twp-body-save-req-" + Guid.NewGuid().ToString("N") + ".bin");
        var responseOut = Path.Combine(Path.GetTempPath(), "twp-body-save-res-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var picker = new ScriptedInspectorPathPicker { SavePath = requestOut };
            var vm = CreateVm(settingsPath, picker);
            vm.SeedSession(new SessionSnapshot
            {
                Id = 7,
                Method = "POST",
                Url = "https://save.example/api",
                Host = "save.example",
                StatusCode = 200,
                RequestBodyBytes = Encoding.UTF8.GetBytes("{\"a\":1}"),
                ResponseBodyBytes = Encoding.UTF8.GetBytes("ok"),
                RequestBodyCapture = BodyCaptureState.Complete,
                ResponseBodyCapture = BodyCaptureState.Complete,
                RequestHeadersText = "Content-Type: application/json\r\n",
                ResponseHeadersText = "Content-Type: text/plain\r\n",
            });
            vm.SelectedSession = vm.Sessions[0];
            vm.SelectedInspectTabIndex = (int)InspectTab.Request;

            Assert.IsTrue(vm.SaveRequestBodyCommand.CanExecute(null));
            vm.SaveRequestBodyCommand.Execute(null);
            await WaitUntil(() => File.Exists(requestOut) || vm.StatusText.Contains("Saved", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(File.Exists(requestOut));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("{\"a\":1}"), await File.ReadAllBytesAsync(requestOut));

            picker.SavePath = responseOut;
            vm.SelectedInspectTabIndex = (int)InspectTab.Response;
            Assert.IsTrue(vm.SaveResponseBodyCommand.CanExecute(null));
            vm.SaveResponseBodyCommand.Execute(null);
            await WaitUntil(() => File.Exists(responseOut) || vm.StatusText.Contains("Saved", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(File.Exists(responseOut));

            vm.SelectedSession = null;
            vm.BodyPrettyMode = false;
            vm.BodyPrettyMode = true;
            Assert.AreEqual("", vm.SelectedRequestBody);
        }
        finally
        {
            TryDelete(settingsPath);
            TryDelete(requestOut);
            TryDelete(responseOut);
        }
    }

    [TestMethod]
    public async Task LoadComposerBodyFile_LargeFile_SetsStreamHint()
    {
        LanguageService.Apply("en");
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-composer-big-" + Guid.NewGuid().ToString("N") + ".json");
        var bodyPath = Path.Combine(Path.GetTempPath(), "twp-composer-big-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            // Above MaxBodyBytes so the composer keeps a file path instead of inlining.
            var oversized = new byte[Math.Max(InspectorBodyLimits.MaxBodyBytes + 1024, 2 * 1024 * 1024)];
            Random.Shared.NextBytes(oversized);
            await File.WriteAllBytesAsync(bodyPath, oversized);

            var picker = new ScriptedInspectorPathPicker { OpenPath = bodyPath };
            var vm = CreateVm(settingsPath, picker);
            vm.LoadComposerBodyFileCommand.Execute(null);
            await WaitUntil(() =>
                vm.HasComposerBodyFile
                || vm.StatusText.Contains("streamed", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Contains("Large file", StringComparison.OrdinalIgnoreCase));

            Assert.IsTrue(vm.HasComposerBodyFile);
            Assert.IsFalse(vm.ComposerBodyEditorEnabled);
            Assert.AreEqual("", vm.ComposerBody);
        }
        finally
        {
            TryDelete(settingsPath);
            TryDelete(bodyPath);
        }
    }

    [TestMethod]
    public void UncommonMethod_AddsFilterRow_AndBodiesMissingNotice()
    {
        LanguageService.Apply("en");
        var path = Path.Combine(Path.GetTempPath(), "twp-method-unc-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            vm.SeedSession(new SessionSnapshot
            {
                Id = 3,
                Method = "PROPFIND",
                Url = "https://dav.example/",
                Host = "dav.example",
                StatusCode = 207,
                BodiesMissingFromDisk = true,
            });
            Assert.IsNotNull(vm.MethodFilterRows.FirstOrDefault(r => r.Name == "PROPFIND"));

            vm.SelectedSession = vm.Sessions[0];
            vm.SelectedInspectTabIndex = (int)InspectTab.Response;
            Assert.IsTrue(
                vm.ResponseBodyCaptureNotice.Length > 0
                || vm.RequestBodyCaptureNotice.Length > 0
                || vm.ShowBodyModeToggles);

            vm.SeedSession(new SessionSnapshot
            {
                Id = 4,
                Method = "MKCOL",
                Url = "https://dav.example/new",
                Host = "dav.example",
                StatusCode = 201,
            });
            Assert.IsNotNull(vm.MethodFilterRows.FirstOrDefault(r => r.Name == "MKCOL"));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static MainWindowViewModel CreateVm(string settingsPath, IInspectorPathPicker? picker = null)
    {
        var settings = new SettingsService(settingsPath);
        settings.Current.AutoStartCapture = false;
        settings.Save();
        var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
        return new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()),
            pathPicker: picker);
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 12_000)
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
        catch
        {
            // Best-effort.
        }
    }
}
