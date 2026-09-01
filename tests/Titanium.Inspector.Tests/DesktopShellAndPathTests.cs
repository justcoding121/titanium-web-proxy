using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.Views;

namespace Titanium.Inspector.Tests;

[TestClass]
public class DesktopShellAndPathTests
{
    [TestMethod]
    public void GetDefaultDirectory_IsUnderLocalAppData_TitaniumInspector_SessionCache()
    {
        var expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = SessionBodyDiskCache.GetDefaultDirectory();
        Assert.IsTrue(Path.IsPathRooted(dir));
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(expectedRoot, "TitaniumInspector", "session-cache")),
            Path.GetFullPath(dir));
    }

    [TestMethod]
    public void DefaultLogPath_IsUnderApplicationData_Logs()
    {
        var expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = LoggingSettingsWindow.DefaultLogPath();
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(expectedRoot, "TitaniumInspector", "logs", "titanium-inspector.log")),
            Path.GetFullPath(path));
    }

    [TestMethod]
    public void SessionStore_DefaultSpillDirectory_MatchesGetDefaultDirectory()
    {
        using var store = new SessionStore(new SessionStoreOptions { SpillBodiesToDisk = true });
        Assert.AreEqual(
            Path.GetFullPath(SessionBodyDiskCache.GetDefaultDirectory()),
            Path.GetFullPath(store.DiskCacheDirectoryPath!));
    }

    [TestMethod]
    public void SessionStore_CustomCacheDirectory_OverridesDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-custom-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions { SpillBodiesToDisk = true },
                dir);
            Assert.AreEqual(Path.GetFullPath(dir), Path.GetFullPath(store.DiskCacheDirectoryPath!));
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [TestMethod]
    public void SessionStore_SpillDisabled_HasNoDiskCacheDirectory()
    {
        using var store = new SessionStore(new SessionStoreOptions { SpillBodiesToDisk = false });
        Assert.IsNull(store.DiskCacheDirectoryPath);
    }

    [TestMethod]
    public void TryBuildOpenDirectory_Empty_Fails()
    {
        Assert.IsFalse(DesktopShell.TryBuildOpenDirectory("  ", OSPlatform.Windows, out _, out var error));
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    public void TryBuildOpenDirectory_Windows_UsesExplorer()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-open-dir");
        Assert.IsTrue(DesktopShell.TryBuildOpenDirectory(path, OSPlatform.Windows, out var cmd, out var error));
        Assert.IsNull(error);
        Assert.AreEqual("explorer.exe", cmd.FileName);
        StringAssert.Contains(cmd.Arguments, Path.GetFullPath(path));
    }

    [TestMethod]
    public void TryBuildOpenDirectory_Mac_UsesOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-open-dir-mac");
        var full = Path.GetFullPath(path);
        Assert.IsTrue(DesktopShell.TryBuildOpenDirectory(path, OSPlatform.OSX, out var cmd, out _));
        Assert.AreEqual("open", cmd.FileName);
        StringAssert.Contains(cmd.Arguments, full);
    }

    [TestMethod]
    public void TryBuildOpenDirectory_Linux_UsesXdgOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-open-dir-linux");
        var full = Path.GetFullPath(path);
        Assert.IsTrue(DesktopShell.TryBuildOpenDirectory(path, OSPlatform.Linux, out var cmd, out _));
        Assert.AreEqual("xdg-open", cmd.FileName);
        StringAssert.Contains(cmd.Arguments, full);
    }

    [TestMethod]
    public void TryBuildRevealFile_Windows_UsesSelect()
    {
        var file = Path.Combine(Path.GetTempPath(), "twp-log", "titanium-inspector.log");
        Assert.IsTrue(DesktopShell.TryBuildRevealFile(file, OSPlatform.Windows, out var cmd, out _));
        Assert.AreEqual("explorer.exe", cmd.FileName);
        StringAssert.StartsWith(cmd.Arguments, "/select,");
        StringAssert.Contains(cmd.Arguments, Path.GetFullPath(file));
    }

    [TestMethod]
    public void TryBuildRevealFile_Mac_UsesOpenR()
    {
        var file = Path.Combine(Path.GetTempPath(), "twp-log-mac", "titanium-inspector.log");
        var full = Path.GetFullPath(file);
        Assert.IsTrue(DesktopShell.TryBuildRevealFile(file, OSPlatform.OSX, out var cmd, out _));
        Assert.AreEqual("open", cmd.FileName);
        StringAssert.StartsWith(cmd.Arguments, "-R ");
        StringAssert.Contains(cmd.Arguments, full);
    }

    [TestMethod]
    public void TryBuildRevealFile_Linux_OpensContainingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-log-linux");
        var file = Path.Combine(dir, "titanium-inspector.log");
        Assert.IsTrue(DesktopShell.TryBuildRevealFile(file, OSPlatform.Linux, out var cmd, out _));
        Assert.AreEqual("xdg-open", cmd.FileName);
        StringAssert.Contains(cmd.Arguments, Path.GetFullPath(dir));
        Assert.IsFalse(cmd.Arguments.Contains("titanium-inspector.log", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryBuildRevealFile_Empty_Fails()
    {
        Assert.IsFalse(DesktopShell.TryBuildRevealFile(null, OSPlatform.Windows, out _, out var error));
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }
}
