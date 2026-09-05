using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy;

namespace Titanium.Inspector.Tests;

[TestClass]
public class ExclusionSettingsSeedTests
{
    [TestMethod]
    public void Load_FreshPath_SeedsFactoryDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-excl-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new SettingsService(path);
            Assert.IsTrue(service.Current.ExclusionsInitialized);
            CollectionAssert.AreEqual(
                MitmExclusionDefaults.SystemProxyBypassRules,
                service.Current.SystemProxyBypassHosts.ToArray());
            CollectionAssert.AreEqual(
                MitmExclusionDefaults.TunnelOnlyPinningDomains,
                service.Current.DecryptSkipHosts.ToArray());
            Assert.IsTrue(service.Current.ProxyLoopback);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void ResetExclusionsToFactoryDefaults_RestoresSeed()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-excl-reset-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new SettingsService(path);
            service.Current.SystemProxyBypassHosts = ["only.example.com"];
            service.Current.DecryptSkipHosts = ["pin.example.com"];
            service.Current.ProxyLoopback = false;
            service.Save();

            service.ResetExclusionsToFactoryDefaults();
            CollectionAssert.AreEqual(
                MitmExclusionDefaults.SystemProxyBypassRules,
                service.Current.SystemProxyBypassHosts.ToArray());
            CollectionAssert.AreEqual(
                MitmExclusionDefaults.TunnelOnlyPinningDomains,
                service.Current.DecryptSkipHosts.ToArray());
            Assert.IsTrue(service.Current.ProxyLoopback);
            Assert.IsTrue(service.Current.ExclusionsInitialized);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void Load_ExistingSettings_MergesMissingFactoryGitBypassHosts()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-excl-merge-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var seed = new InspectorSettings
            {
                ExclusionsInitialized = true,
                SystemProxyBypassHosts = ["login.live.com"],
                DecryptSkipHosts = ["dropbox.com"],
                ProxyLoopback = true,
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(seed));

            var service = new SettingsService(path);
            Assert.IsTrue(service.Current.SystemProxyBypassHosts.Contains("login.live.com"));
            Assert.IsTrue(service.Current.SystemProxyBypassHosts.Contains("github.com"),
                "Existing settings must pick up new factory forge bypass hosts");
            Assert.IsTrue(service.Current.SystemProxyBypassHosts.Contains("*.github.com"));
            CollectionAssert.Contains(service.Current.DecryptSkipHosts.ToArray(), "dropbox.com");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void ExclusionSummary_CountsUserLists()
    {
        var settings = new InspectorSettings
        {
            SystemProxyBypassHosts = ["a.com", "b.com"],
            DecryptSkipHosts = ["c.com"],
        };
        Assert.AreEqual("Exclusions: 2 OS bypass, 1 tunnel-only", ExclusionPreview.ExclusionSummary(settings));
    }

    [TestMethod]
    public void FormatForCurrentOs_ReturnsNonEmptyPreview()
    {
        var settings = new InspectorSettings();
        SettingsService.ApplyFactoryExclusionDefaults(settings);
        var (label, value) = ExclusionPreview.FormatForCurrentOs(settings);
        Assert.IsFalse(string.IsNullOrWhiteSpace(label));
        Assert.IsTrue(value.Contains("login.live.com", StringComparison.OrdinalIgnoreCase)
                      || value.Contains("microsoftonline", StringComparison.OrdinalIgnoreCase));
    }
}
