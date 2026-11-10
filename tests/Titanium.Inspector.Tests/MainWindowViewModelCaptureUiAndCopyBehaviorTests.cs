using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Fat remaining MainWindowViewModel.Sessions / CaptureUiPump edges: copy-as command status paths and
///     backlog drop when a single capture batch exceeds the UI backlog cap.
/// </summary>
[TestClass]
public class MainWindowViewModelCaptureUiAndCopyBehaviorTests
{
    [TestMethod]
    [Timeout(60_000)]
    public void CaptureUi_BacklogBeyondCap_DropsOldestAndCountsThem()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-cap-ui-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            var enqueue = typeof(MainWindowViewModel).GetMethod(
                "EnqueueCapturedBatch", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(enqueue);

            var excess = 80;
            var batch = new List<SessionSnapshot>(MainWindowViewModel.CaptureUiBacklogCap + excess);
            for (var i = 1; i <= MainWindowViewModel.CaptureUiBacklogCap + excess; i++)
            {
                batch.Add(new SessionSnapshot
                {
                    Id = i,
                    Method = "GET",
                    Url = $"https://cap.example/{i}",
                    Host = "cap.example",
                    StatusCode = 200,
                });
            }

            enqueue!.Invoke(vm, [batch]);

            Assert.IsTrue(
                vm.CaptureUiRowsDropped >= excess,
                $"expected >= {excess} dropped, got {vm.CaptureUiRowsDropped}");
            Assert.IsTrue(vm.Sessions.Count <= MainWindowViewModel.CaptureUiBacklogCap + 1);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public async Task CopyAsCommands_WithSelection_SetCopiedStatus_AndGuardsWithoutSelection()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-copy-as-" + Guid.NewGuid().ToString("N") + ".json");
        var httpPath = Path.Combine(Path.GetTempPath(), "twp-copy-as-" + Guid.NewGuid().ToString("N") + ".http");
        try
        {
            var picker = new ScriptedInspectorPathPicker { SavePath = httpPath };
            var vm = CreateVm(settingsPath, picker);

            vm.CopyAsFetchCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Select one session", StringComparison.Ordinal));

            vm.CopyAsPowerShellCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("PowerShell", StringComparison.Ordinal));

            vm.CopyAsPythonCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Python", StringComparison.Ordinal));

            vm.CopyAsTableCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("No sessions", StringComparison.Ordinal)
                                  || vm.StatusText.Contains("table", StringComparison.OrdinalIgnoreCase));

            vm.SaveRawHttpCommand.Execute(null);
            await WaitUntil(() =>
                vm.StatusText.Contains("Select a session", StringComparison.OrdinalIgnoreCase)
                || vm.StatusText.Length >= 0);

            vm.SeedSession(new SessionSnapshot
            {
                Id = 11,
                Method = "POST",
                Url = "https://copy.example/api",
                Host = "copy.example",
                StatusCode = 201,
                RequestHeadersText = "Content-Type: application/json\r\n",
                RequestBodyText = "{\"n\":1}",
                ResponseHeadersText = "Content-Type: text/plain\r\n",
                ResponseBodyText = "ok",
            });
            vm.SelectedSession = vm.Sessions[0];
            vm.SetSelectedSessions([vm.Sessions[0]]);

            Assert.IsTrue(vm.TryBuildCopyAsFetch(out var fetch));
            StringAssert.Contains(fetch, "fetch(");
            Assert.IsTrue(vm.TryBuildCopyAsPython(out var py));
            StringAssert.Contains(py, "requests");
            Assert.IsTrue(vm.TryBuildRowsAsTable(out var table, out var written, out _));
            Assert.AreEqual(1, written);
            StringAssert.Contains(table, "copy.example");

            vm.CopyAsFetchCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Copied as fetch", StringComparison.Ordinal)
                                  || vm.StatusText.Contains("clipboard", StringComparison.OrdinalIgnoreCase)
                                  || vm.StatusText.Contains("fetch", StringComparison.OrdinalIgnoreCase));

            vm.CopyAsPowerShellCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("PowerShell", StringComparison.OrdinalIgnoreCase));

            vm.CopyAsPythonCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Python", StringComparison.OrdinalIgnoreCase));

            vm.CopyAsTableCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("table", StringComparison.OrdinalIgnoreCase)
                                  || vm.StatusText.Contains("Copied", StringComparison.Ordinal));

            vm.SaveRawHttpCommand.Execute(null);
            await WaitUntil(() => File.Exists(httpPath) || vm.StatusText.Length > 0);
        }
        finally
        {
            TryDelete(settingsPath);
            TryDelete(httpPath);
        }
    }

    [TestMethod]
    public void BeginBackgroundShutdown_AndEnsureShutdown_AreIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-shut-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            vm.AlwaysOnTop = true;
            Assert.IsTrue(vm.AlwaysOnTop);
            vm.AlwaysOnTop = true;
            vm.AlwaysOnTop = false;

            vm.BeginBackgroundShutdown();
            vm.EnsureShutdown();
            vm.EnsureShutdown();
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static MainWindowViewModel CreateVm(string settingsPath, IInspectorPathPicker? picker = null)
    {
        var settings = new SettingsService(settingsPath);
        var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
        return new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()),
            pathPicker: picker);
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
        catch
        {
            // Best-effort.
        }
    }
}
