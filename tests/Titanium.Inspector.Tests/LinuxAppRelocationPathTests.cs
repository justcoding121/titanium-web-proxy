using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class LinuxAppRelocationPathTests
{
    [TestMethod]
    public void DefaultInstallDir_UsesDotLocalShare()
    {
        // Pure Path.Combine helpers — exercise on every OS for coverage of the .local constant.
#pragma warning disable CA1416
        var path = LinuxAppRelocation.DefaultInstallDir();
#pragma warning restore CA1416
        StringAssert.Contains(path, ".local");
        StringAssert.Contains(path, "share");
        StringAssert.Contains(path, LinuxAppRelocation.AppDirName);
    }

    [TestMethod]
    public void DefaultWrapperPath_UsesDotLocalBin()
    {
#pragma warning disable CA1416
        var path = LinuxAppRelocation.DefaultWrapperPath();
#pragma warning restore CA1416
        StringAssert.Contains(path, ".local");
        StringAssert.Contains(path, "bin");
        StringAssert.Contains(path, "titanium-inspector");
    }
}
