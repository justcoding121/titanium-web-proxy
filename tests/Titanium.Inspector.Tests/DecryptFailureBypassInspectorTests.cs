using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.Views;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

[TestClass]
public class DecryptFailureBypassInspectorTests
{
    [TestMethod]
    public void Settings_DefaultEnableDecryptFailureBypass_True()
    {
        var s = new InspectorSettings();
        Assert.IsTrue(s.EnableDecryptFailureBypass);
    }

    [TestMethod]
    public void Settings_PersistsEnableDecryptFailureBypass()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-learn-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new SettingsService(path);
            svc.Current.EnableDecryptFailureBypass = false;
            svc.Save();
            var reloaded = new SettingsService(path);
            Assert.IsFalse(reloaded.Current.EnableDecryptFailureBypass);

            reloaded.Current.EnableDecryptFailureBypass = true;
            reloaded.Save();
            var again = new SettingsService(path);
            Assert.IsTrue(again.Current.EnableDecryptFailureBypass);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void DescribeOpaqueReason_LearnedFailure()
    {
        Assert.IsTrue(ExclusionPreview.DescribeOpaqueReason(OpaqueTunnelReason.LearnedFailure)
            .Contains("auto-tunneled", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ExclusionSummary_IncludesLearnedCount()
    {
        var s = new InspectorSettings
        {
            SystemProxyBypassHosts = [],
            DecryptSkipHosts = [],
        };
        Assert.AreEqual("", ExclusionPreview.ExclusionSummary(s));
        Assert.IsTrue(ExclusionPreview.ExclusionSummary(s, learnedCount: 3).Contains("Learned: 3"));
    }

    [TestMethod]
    public void InterceptionService_DefaultLearningOn()
    {
        using var svc = new InterceptionService(new RecordingSystemProxyController());
        Assert.IsTrue(svc.EnableDecryptFailureBypass);
    }

    [TestMethod]
    public void FormatLearnedDisplay_IncludesHostAndReason()
    {
        var entry = new DecryptFailureBypassEntry(
            "pin.example.com",
            strikes: 2,
            learnedAtUtc: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            expiresAtUtc: DateTime.UtcNow.AddMinutes(30),
            bypassActive: true);
        var display = ExcludedHostsWindow.FormatLearnedDisplay(entry);
        StringAssert.Contains(display, "pin.example.com");
        StringAssert.Contains(display, "Decrypt failure");
        StringAssert.Contains(display, "2026-01-02");
    }

    [TestMethod]
    public async Task ForceLearnDecryptBypass_RaisesLearnedAndListsHost()
    {
        using var svc = new InterceptionService(new RecordingSystemProxyController());
        await svc.StartAsync(System.Net.IPAddress.Loopback, port: 0);
        DecryptFailureBypassEntry? learned = null;
        svc.DecryptFailureBypassLearned += (_, e) => learned = e;

        Assert.IsTrue(svc.ForceLearnDecryptBypass("live-learn.example"));
        Assert.IsNotNull(learned);
        Assert.AreEqual("live-learn.example", learned!.Host);
        Assert.IsTrue(svc.GetDecryptFailureBypassEntries().Any(e =>
            e.BypassActive &&
            string.Equals(e.Host, "live-learn.example", StringComparison.OrdinalIgnoreCase)));
    }
}
