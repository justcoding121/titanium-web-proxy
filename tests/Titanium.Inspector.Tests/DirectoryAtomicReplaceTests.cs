using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class DirectoryAtomicReplaceTests
{
    [TestMethod]
    public void Commit_SwapsStagingIntoDest_AndDeletesBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "ti-atomic-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "app");
        var staging = Path.Combine(root, "app.tmp");
        Directory.CreateDirectory(dest);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(dest, "old.txt"), "old");
        File.WriteAllText(Path.Combine(staging, "new.txt"), "new");
        try
        {
            DirectoryAtomicReplace.Commit(staging, dest);
            Assert.IsTrue(File.Exists(Path.Combine(dest, "new.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(dest, "old.txt")));
            Assert.IsFalse(Directory.Exists(staging));
            Assert.AreEqual(0, Directory.GetDirectories(root, "app.old-*").Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void Commit_FailureRestoresPreviousDest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ti-atomic-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "app");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "safe");
        var missingStaging = Path.Combine(root, "missing-staging");
        try
        {
            Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
                DirectoryAtomicReplace.Commit(missingStaging, dest));
            Assert.IsTrue(File.Exists(Path.Combine(dest, "keep.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void TryDelete_MissingPath_IsNoOp()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ti-atomic-missing-" + Guid.NewGuid());
        Assert.IsFalse(Directory.Exists(missing));
        DirectoryAtomicReplace.TryDelete(missing);
        Assert.IsFalse(Directory.Exists(missing));
    }
}
