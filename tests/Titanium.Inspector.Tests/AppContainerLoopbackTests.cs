using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Windows FirewallAPI / AppContainer loopback P/Invoke smoke (runs on windows-latest CI).
/// </summary>
[TestClass]
public class AppContainerLoopbackTests
{
    [TestMethod]
    public void TryProbeApis_ResolvesFirewallAndSidConversion_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only");
            return;
        }

        Assert.IsTrue(AppContainerLoopback.IsSupported);
        Assert.IsTrue(AppContainerLoopback.TryProbeApis(out var message), message);
        StringAssert.Contains(message, "ConvertStringSidToSidW ok");
    }

    [TestMethod]
    public void ListContainers_DoesNotThrow_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only");
            return;
        }

        var list = AppContainerLoopback.ListContainers();
        Assert.IsNotNull(list);
        foreach (var item in list)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.AppContainerSid));
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.PackageFamilyName) &&
                           string.IsNullOrWhiteSpace(item.DisplayName));
        }
    }

    [TestMethod]
    public void SetExemptions_IdentityReapply_DoesNotThrowEntryPointNotFound_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only");
            return;
        }

        // Re-apply the current exemption set (no intentional mutation). Exercises
        // ConvertStringSidToSidW + NetworkIsolationSetAppContainerConfig. May return
        // false without elevation; must not throw EntryPointNotFoundException.
        var current = AppContainerLoopback.ListContainers()
            .Where(c => c.IsExempt)
            .Select(c => c.AppContainerSid)
            .ToList();

        try
        {
            _ = AppContainerLoopback.SetExemptions(current);
        }
        catch (EntryPointNotFoundException ex)
        {
            Assert.Fail("P/Invoke entry point missing: " + ex.Message);
        }
    }
}
