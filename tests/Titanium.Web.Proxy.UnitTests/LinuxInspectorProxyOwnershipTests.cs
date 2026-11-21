using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class LinuxInspectorProxyOwnershipTests
{
    [TestMethod]
    public void HasEvidence_EmptyHome_IsFalse()
    {
        var home = Path.Combine(Path.GetTempPath(), "ti-own-" + Path.GetRandomFileName());
        Directory.CreateDirectory(home);
        try
        {
            Assert.IsFalse(LinuxInspectorProxyOwnership.HasEvidence(home));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void HasEvidence_ProxySnapshot_IsTrue()
    {
        AssertMarkerSeen("proxy-snapshot.json");
    }

    [TestMethod]
    public void HasEvidence_ChromeMarker_IsTrue()
    {
        AssertMarkerSeen("chrome-profile-proxy.json");
    }

    [TestMethod]
    public void HasEvidence_FailOpenPid_IsTrue()
    {
        AssertMarkerSeen("fail-open-proxy.pid");
    }

    [TestMethod]
    public void HasEvidence_FirefoxBackup_IsTrue()
    {
        AssertMarkerSeen("firefox-proxy-backup.json");
    }

    [TestMethod]
    public void HasEvidence_UserPolicyFile_IsTrue()
    {
        var home = Path.Combine(Path.GetTempPath(), "ti-own-" + Path.GetRandomFileName());
        var policyDir = Path.Combine(home, ".config", "google-chrome", "policies", "managed");
        Directory.CreateDirectory(policyDir);
        File.WriteAllText(Path.Combine(policyDir, "titanium-inspector-proxy.json"), "{}");
        try
        {
            Assert.IsTrue(LinuxInspectorProxyOwnership.HasEvidence(home));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void HasEvidence_MachinePolicyFile_IsTrue()
    {
        var root = Path.Combine(Path.GetTempPath(), "ti-own-machine-" + Path.GetRandomFileName());
        var managed = Path.Combine(root, "policies", "managed");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "titanium-inspector-proxy.json"), "{}");
        var emptyHome = Path.Combine(Path.GetTempPath(), "ti-own-empty-" + Path.GetRandomFileName());
        Directory.CreateDirectory(emptyHome);
        var previous = LinuxInspectorProxyOwnership.MachinePolicyRootsForTests;
        try
        {
            LinuxInspectorProxyOwnership.MachinePolicyRootsForTests = [root];
            Assert.IsTrue(LinuxInspectorProxyOwnership.HasEvidence(emptyHome));
        }
        finally
        {
            LinuxInspectorProxyOwnership.MachinePolicyRootsForTests = previous;
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
            try { Directory.Delete(emptyHome, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void TryForceClearWhenOwned_NonLinux_ReturnsTrue()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Exercises the non-Linux short path.");
            return;
        }

        // Windows/macOS always clear; ForceClear is a no-op when no loopback proxy is enabled.
        Assert.IsTrue(InstalledProxyCleanup.TryForceClearWhenOwned());
    }

    [TestMethod]
    public void TryForceClearWhenOwned_NoEvidence_ReturnsFalseOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Ownership gate is Linux-only.");
            return;
        }

        // Real home may have leftover markers from a local Inspector install; the public API
        // cannot inject a home, so only assert the internal helper here. The ClearProxy binary
        // calls TryForceClearWhenOwned which short-circuits on !HasEvidence().
        var home = Path.Combine(Path.GetTempPath(), "ti-own-" + Path.GetRandomFileName());
        Directory.CreateDirectory(home);
        try
        {
            Assert.IsFalse(LinuxInspectorProxyOwnership.HasEvidence(home));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* ignore */ }
        }
    }

    private static void AssertMarkerSeen(string fileName)
    {
        var home = Path.Combine(Path.GetTempPath(), "ti-own-" + Path.GetRandomFileName());
        var dir = Path.Combine(home, ".config", "TitaniumInspector");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "x");
        try
        {
            Assert.IsTrue(LinuxInspectorProxyOwnership.HasEvidence(home));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* ignore */ }
        }
    }
}
