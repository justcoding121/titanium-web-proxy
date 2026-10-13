using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class CustomSettingsReportTests
{
    [TestMethod]
    public void EachDeviation_ShowsAndClears_AndResetRestoresOnlyThoseSettings()
    {
        var settings = new InspectorSettings
        {
            BindPort = 9000,
            ThemeMode = ThemeMode.Dark,
        };
        Assert.AreEqual(0, CustomSettingsReport.ListDeviations(settings).Count);

        settings.UpstreamProxyEnabled = true;
        settings.UpstreamProxyHost = "10.1.2.3";
        settings.UpstreamProxyPort = 9090;
        AssertKeys(settings, CustomSettingsReport.Upstream);
        Assert.AreEqual("http://10.1.2.3:9090", CustomSettingsReport.ListDeviations(settings)[0].Suffix);
        settings.UpstreamProxyEnabled = false;
        settings.UpstreamProxyHost = "10.1.2.3";
        Assert.AreEqual(0, CustomSettingsReport.ListDeviations(settings).Count);

        settings.EnableHttp2 = false;
        AssertKeys(settings, CustomSettingsReport.Http2Off);
        settings.EnableHttp2 = true;
        Assert.AreEqual(0, CustomSettingsReport.ListDeviations(settings).Count);

        settings.EnableHttp3 = false;
        AssertKeys(settings, CustomSettingsReport.Http3Off);
        settings.EnableHttp3 = true;
        Assert.AreEqual(0, CustomSettingsReport.ListDeviations(settings).Count);

        settings.OutboundTlsPreset = InspectorNetworkSettings.Tls12;
        AssertKeys(settings, "network.tls12");
        settings.OutboundTlsPreset = InspectorNetworkSettings.TlsLegacy;
        AssertKeys(settings, "network.tlsLegacy");
        settings.OutboundTlsPreset = "custom";
        AssertKeys(settings, "network.tls");
        settings.OutboundTlsPreset = InspectorNetworkSettings.TlsDefault;
        Assert.AreEqual(0, CustomSettingsReport.ListDeviations(settings).Count);

        settings.ReuseServerConnections = false;
        AssertKeys(settings, CustomSettingsReport.ReuseOff);
        settings.ReuseServerConnections = true;
        Assert.AreEqual(0, CustomSettingsReport.ListDeviations(settings).Count);

        settings.UpstreamProxyEnabled = true;
        settings.UpstreamProxyHost = "proxy.example";
        settings.UpstreamProxyPort = 9;
        settings.EnableHttp2 = false;
        settings.EnableHttp3 = false;
        settings.OutboundTlsPreset = InspectorNetworkSettings.Tls12;
        settings.ReuseServerConnections = false;
        Assert.AreEqual(5, CustomSettingsReport.ListDeviations(settings).Count);

        settings.WinAuthHosts = "corp.example";
        CollectionAssert.Contains(
            CustomSettingsReport.ListDeviations(settings).Select(item => item.TextKey).ToArray(),
            "network.winAuth");
        settings.WinAuthHosts = "  ";
        Assert.IsFalse(CustomSettingsReport.ListDeviations(settings).Any(d => d.TextKey == "network.winAuth"));
        settings.WinAuthHosts = "corp.example";

        CustomSettingsReport.RestoreDefaults(settings);
        Assert.AreEqual(0, CustomSettingsReport.ListDeviations(settings).Count);
        Assert.IsFalse(settings.UpstreamProxyEnabled);
        Assert.AreEqual(string.Empty, settings.UpstreamProxyHost);
        Assert.AreEqual(InspectorNetworkSettings.DefaultUpstreamPort, settings.UpstreamProxyPort);
        Assert.IsTrue(settings.EnableHttp2);
        Assert.IsTrue(settings.EnableHttp3);
        Assert.AreEqual(InspectorNetworkSettings.TlsDefault, settings.OutboundTlsPreset);
        Assert.IsTrue(settings.ReuseServerConnections);
        Assert.AreEqual(string.Empty, settings.WinAuthHosts);
        Assert.AreEqual(9000, settings.BindPort);
        Assert.AreEqual(ThemeMode.Dark, settings.ThemeMode);
    }

    [TestMethod]
    public void ResetCommand_ClearsTheIndicator_AndAppliesProtocolDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.AutoStartCapture = false;
        settings.Current.EnableHttp2 = false;
        settings.Current.ReuseServerConnections = false;
        settings.Current.ThemeMode = ThemeMode.Dark;
        settings.Save();

        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        var registry = new SessionRegistry();
        var vm = new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            interception,
            new ScriptedInspectorDialogs(),
            new ScriptedInspectorPathPicker());

        Assert.IsTrue(vm.HasCustomSettings);
        Assert.AreEqual(2, vm.CustomSettingLines.Count);
        Assert.IsFalse(interception.PreferHttp2);
        Assert.IsFalse(interception.ReuseServerConnections);
        StringAssert.Contains(vm.CustomSettingsLabel, "2");

        vm.ResetCustomSettingsCommand.Execute(null);

        Assert.IsFalse(vm.HasCustomSettings);
        Assert.AreEqual(0, vm.CustomSettingLines.Count);
        Assert.AreEqual(string.Empty, vm.CustomSettingsLabel);
        Assert.IsTrue(settings.Current.EnableHttp2);
        Assert.IsTrue(settings.Current.ReuseServerConnections);
        Assert.IsTrue(interception.PreferHttp2);
        Assert.IsTrue(interception.ReuseServerConnections);
        Assert.AreEqual(ThemeMode.Dark, settings.Current.ThemeMode);
    }

    private static void AssertKeys(InspectorSettings settings, params string[] keys)
    {
        CollectionAssert.AreEqual(keys, CustomSettingsReport.ListDeviations(settings).Select(item => item.TextKey).ToArray());
    }
}
