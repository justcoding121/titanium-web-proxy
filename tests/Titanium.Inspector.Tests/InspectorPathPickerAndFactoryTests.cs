using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InspectorPathPickerAndFactoryTests
{
    [TestMethod]
    public async Task ScriptedPathPicker_ReturnsConfiguredPaths_AndCountsCalls()
    {
        var picker = new ScriptedInspectorPathPicker
        {
            SavePath = @"C:\tmp\out.har",
            OpenPath = @"C:\tmp\in.har",
        };

        Assert.AreEqual(@"C:\tmp\out.har", await picker.PickSavePathAsync("t", "x.har", "HAR", "*.har"));
        Assert.AreEqual(@"C:\tmp\in.har", await picker.PickOpenPathAsync("t", "HAR", "*.har"));
        Assert.AreEqual(1, picker.SaveCalls);
        Assert.AreEqual(1, picker.OpenCalls);
        Assert.AreEqual(1, picker.LastSaveFileTypes!.Count);
        Assert.AreEqual("HAR", picker.LastSaveFileTypes[0].Name);

        picker.SavePath = @"C:\tmp\ca.pem";
        Assert.AreEqual(
            @"C:\tmp\ca.pem",
            await picker.PickSavePathAsync(
                "Export root CA",
                "TitaniumInspector-RootCA.cer",
                [
                    new PathPickerFileType("Certificate", "*.cer"),
                    new PathPickerFileType("PEM", "*.pem"),
                ]));
        Assert.AreEqual(2, picker.SaveCalls);
        Assert.AreEqual(2, picker.LastSaveFileTypes!.Count);
        Assert.AreEqual("*.pem", picker.LastSaveFileTypes[1].Pattern);

        picker.SavePath = null;
        picker.OpenPath = null;
        Assert.IsNull(await picker.PickSavePathAsync("t", "x.har", "HAR", "*.har"));
        Assert.IsNull(await picker.PickOpenPathAsync("t", "HAR", "*.har"));
        Assert.AreEqual(3, picker.SaveCalls);
        Assert.AreEqual(2, picker.OpenCalls);
    }

    [TestMethod]
    public async Task AvaloniaPathPicker_WithoutUi_FallsBackToDesktopSavePath()
    {
        var picker = new AvaloniaInspectorPathPicker();
        var path = await picker.PickSavePathAsync("Export", "sample.har", "HAR", "*.har");
        Assert.IsNotNull(path);
        StringAssert.Contains(path!, "sample");
        StringAssert.EndsWith(path, ".har");
        Assert.IsTrue(Path.IsPathRooted(path));
    }

    [TestMethod]
    public async Task AvaloniaPathPicker_WithoutUi_UsesBraceSuggestedNameAsIs()
    {
        var picker = new AvaloniaInspectorPathPicker();
        var suggested = "report-{stamp}.har";
        var path = await picker.PickSavePathAsync("Export", suggested, "HAR", "*.har");
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), suggested), path);
    }

    [TestMethod]
    public async Task AvaloniaPathPicker_WithoutUi_OpenReturnsNullWhenDesktopHasNoMatch()
    {
        var picker = new AvaloniaInspectorPathPicker();
        var path = await picker.PickOpenPathAsync(
            "Open",
            "NOMATCH",
            $"twp-no-such-file-{Guid.NewGuid():N}.har");
        Assert.IsNull(path);
    }

    [TestMethod]
    public async Task AvaloniaPathPicker_WithoutUi_OpenFindsDesktopMatch()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var name = $"twp-picker-{Guid.NewGuid():N}.har";
        var full = Path.Combine(desktop, name);
        await File.WriteAllTextAsync(full, "{}");
        try
        {
            var picker = new AvaloniaInspectorPathPicker();
            var path = await picker.PickOpenPathAsync("Open", "HAR", name);
            Assert.AreEqual(full, path);
        }
        finally
        {
            File.Delete(full);
        }
    }

    [TestMethod]
    public void InspectorAppFactory_CreatesViewModelWithInjectedPathPicker()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-factory-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry();
            var buffer = new SessionStreamBuffer(registry);
            var updates = new UpdateService(settings);
            var picker = new ScriptedInspectorPathPicker();

            var vm = InspectorAppFactory.CreateViewModel(
                settings,
                buffer,
                registry,
                updates,
                new InterceptionService(new RecordingSystemProxyController()),
                pathPicker: picker);

            Assert.AreSame(picker, vm.PathPicker);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [TestMethod]
    public async Task ExportImportCommands_HonourScriptedPathPickerCancelAndSuccess()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-export-" + Guid.NewGuid().ToString("N") + ".json");
        var harPath = Path.Combine(Path.GetTempPath(), $"twp-export-{Guid.NewGuid():N}.har");
        var zipPath = Path.Combine(Path.GetTempPath(), $"twp-export-{Guid.NewGuid():N}.zip");
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry();
            var picker = new ScriptedInspectorPathPicker();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()),
                pathPicker: picker);

            await ExecuteAsync(vm.ExportHarCommand);
            StringAssert.Contains(vm.StatusText, "No sessions to export");

            await ExecuteAsync(vm.ExportSelectedHarCommand);
            StringAssert.Contains(vm.StatusText, "Select a session to export");

            vm.SeedSession(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "http://example/",
                StatusCode = 200,
            });

            picker.SavePath = null;
            await ExecuteAsync(vm.ExportHarCommand);
            StringAssert.Contains(vm.StatusText, "cancelled");

            picker.OpenPath = null;
            await ExecuteAsync(vm.ImportHarCommand);
            StringAssert.Contains(vm.StatusText, "import");

            picker.SavePath = null;
            await ExecuteAsync(vm.ExportArchiveCommand);
            StringAssert.Contains(vm.StatusText, "cancelled");

            picker.OpenPath = null;
            await ExecuteAsync(vm.ImportArchiveCommand);
            StringAssert.Contains(vm.StatusText, "archive");

            picker.SavePath = harPath;
            await ExecuteAsync(vm.ExportHarCommand);
            StringAssert.Contains(vm.StatusText, "Exported 1 sessions");
            Assert.IsTrue(File.Exists(harPath));

            picker.OpenPath = harPath;
            await ExecuteAsync(vm.ImportHarCommand);
            StringAssert.Contains(vm.StatusText, "Appended");

            picker.SavePath = zipPath;
            await ExecuteAsync(vm.ExportArchiveCommand);
            StringAssert.Contains(vm.StatusText, "Exported");
            Assert.IsTrue(File.Exists(zipPath));

            picker.OpenPath = zipPath;
            await ExecuteAsync(vm.ImportArchiveCommand);
            StringAssert.Contains(vm.StatusText, "Appended");
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (File.Exists(harPath))
            {
                File.Delete(harPath);
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }

    [TestMethod]
    public async Task ExportSelected_WritesOnlySelectedSubset()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-sel-export-" + Guid.NewGuid().ToString("N") + ".json");
        var harPath = Path.Combine(Path.GetTempPath(), $"twp-sel-export-{Guid.NewGuid():N}.har");
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry();
            var picker = new ScriptedInspectorPathPicker { SavePath = harPath };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()),
                pathPicker: picker);

            var keep = new SessionSnapshot { Id = 10, Method = "GET", Url = "http://keep/", StatusCode = 200 };
            var drop = new SessionSnapshot { Id = 11, Method = "POST", Url = "http://drop/", StatusCode = 201 };
            vm.SeedSession(keep);
            vm.SeedSession(drop);
            vm.SetSelectedSessions([keep]);

            await ExecuteAsync(vm.ExportSelectedHarCommand);
            StringAssert.Contains(vm.StatusText, "Exported 1 sessions");
            Assert.IsTrue(File.Exists(harPath));

            var imported = await SessionArchive.ImportHarAsync(harPath);
            Assert.AreEqual(1, imported.Count);
            Assert.AreEqual("http://keep/", imported[0].Url);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (File.Exists(harPath))
            {
                File.Delete(harPath);
            }
        }
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        // RelayCommand.Execute is async void — give the continuation time to update StatusText.
        await Task.Delay(200);
    }
}
