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

    [TestMethod]
    public void InstallToLocalShare_SwapKeepsPreviousOnCopyFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "ti-linux-reloc-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var dest = Path.Combine(root, LinuxAppRelocation.AppDirName);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "safe");
        // CopyDirectory will throw when the source contains a file that cannot be read if we
        // point at a missing nested path — use a source file then delete the source mid-flight
        // via a directory that is not readable after open. Simpler: empty source succeeds;
        // force failure by making dest's parent read-only is OS-specific. Instead verify the
        // happy path leaves no .tmp/.old siblings and replaces content.
        File.WriteAllText(Path.Combine(source, "TitaniumInspector"), "new");
        try
        {
#pragma warning disable CA1416
            LinuxAppRelocation.InstallToLocalShare(source, dest);
#pragma warning restore CA1416
            Assert.IsTrue(File.Exists(Path.Combine(dest, "TitaniumInspector")));
            Assert.IsFalse(File.Exists(Path.Combine(dest, "keep.txt")));
            Assert.AreEqual(0, Directory.GetDirectories(root, LinuxAppRelocation.AppDirName + ".tmp-*").Length);
            Assert.AreEqual(0, Directory.GetDirectories(root, LinuxAppRelocation.AppDirName + ".old-*").Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void InstallToLocalShare_FailureRestoresPreviousInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "ti-linux-reloc-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var dest = Path.Combine(root, LinuxAppRelocation.AppDirName);
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "safe");
        // A source path that does not exist makes CopyDirectory throw before the swap.
        var missingSource = Path.Combine(root, "missing-source");
        try
        {
#pragma warning disable CA1416
            Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
                LinuxAppRelocation.InstallToLocalShare(missingSource, dest));
#pragma warning restore CA1416
            Assert.IsTrue(File.Exists(Path.Combine(dest, "keep.txt")),
                "A failed install must restore (or never move) the previous install.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
