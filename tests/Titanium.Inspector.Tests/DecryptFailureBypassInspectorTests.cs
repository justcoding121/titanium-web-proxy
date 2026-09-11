using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

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
}
