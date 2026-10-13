using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class AlwaysOnTopTests
{
    [TestMethod]
    public void Setting_RoundTrips_AndOldFilesStayOff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-top-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """{"bindAddress":"127.0.0.1","bindPort":8866}""");
        var loaded = new SettingsService(path);
        Assert.IsFalse(loaded.Current.AlwaysOnTop);

        loaded.Current.AlwaysOnTop = true;
        loaded.Save();
        var again = new SettingsService(path);
        Assert.IsTrue(again.Current.AlwaysOnTop);

        again.ResetToFactoryDefaults();
        Assert.IsFalse(again.Current.AlwaysOnTop);
        Assert.IsFalse(new SettingsService(path).Current.AlwaysOnTop);
    }

    [TestMethod]
    public void ResetSettings_TurnsAlwaysOnTopOff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-top-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        var settings = new SettingsService(path);
        settings.Current.AutoStartCapture = false;
        settings.Current.AlwaysOnTop = true;
        settings.Save();
        var dialogs = new ScriptedInspectorDialogs();
        var vm = NewVm(settings, dialogs);
        Assert.IsTrue(vm.AlwaysOnTop);

        vm.AlwaysOnTop = false;
        Assert.IsFalse(new SettingsService(path).Current.AlwaysOnTop);
        vm.AlwaysOnTop = true;

        dialogs.ResetSettingsResult = false;
        vm.ResetSettingsCommand.Execute(null);
        Assert.IsTrue(vm.AlwaysOnTop);

        dialogs.ResetSettingsResult = true;
        vm.ResetSettingsCommand.Execute(null);
        Assert.IsFalse(vm.AlwaysOnTop);
        Assert.IsFalse(new SettingsService(path).Current.AlwaysOnTop);
    }

    private static MainWindowViewModel NewVm(SettingsService settings, ScriptedInspectorDialogs dialogs)
    {
        var registry = new SessionRegistry();
        return new MainWindowViewModel(new InspectorViewModelServices(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true },
            dialogs,
            new ScriptedInspectorPathPicker()));
    }
}
