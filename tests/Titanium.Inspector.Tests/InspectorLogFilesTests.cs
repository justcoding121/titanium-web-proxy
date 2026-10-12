using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InspectorLogFilesTests
{
    [TestMethod]
    public void NewFileName_UsesReadableTimestamp()
    {
        var name = InspectorLogFiles.NewFileName(new DateTime(2026, 10, 12, 14, 30, 5));
        Assert.AreEqual("titanium-inspector-2026-10-12_14-30-05.log", name);
    }

    [TestMethod]
    public void UniquePath_AddsSuffixWhenNameExists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-logfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = new DateTime(2026, 10, 12, 14, 30, 5);
            var first = InspectorLogFiles.UniquePath(dir, now);
            File.WriteAllText(first, "a");
            var second = InspectorLogFiles.UniquePath(dir, now);
            Assert.AreEqual(Path.Combine(dir, "titanium-inspector-2026-10-12_14-30-05_2.log"), second);
            Assert.AreNotEqual(first, second);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Prune_KeepsNewestByWriteTime_NeverDeletesCurrent_IgnoresUnrelated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var unrelated = Path.Combine(dir, "notes.txt");
            File.WriteAllText(unrelated, "keep");
            var legacy = Path.Combine(dir, "titanium-inspector.log");
            File.WriteAllText(legacy, "old");
            File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddMinutes(-10));
            var older = Path.Combine(dir, "titanium-inspector-2026-10-01_00-00-00.log");
            File.WriteAllText(older, "older");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
            var current = Path.Combine(dir, "titanium-inspector-2026-10-12_14-30-05.log");
            File.WriteAllText(current, "current");
            File.SetLastWriteTimeUtc(current, DateTime.UtcNow);

            InspectorLogFiles.Prune(dir, keep: 2, currentFile: current);

            Assert.IsTrue(File.Exists(unrelated));
            Assert.IsTrue(File.Exists(current));
            Assert.IsTrue(File.Exists(older));
            Assert.IsFalse(File.Exists(legacy));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void MigrateFilePathToFolder_StripsLogFileName()
    {
        Assert.AreEqual(@"C:\logs", InspectorLogFiles.MigrateFilePathToFolder(@"C:\logs\titanium-inspector.log"));
        Assert.AreEqual(@"C:\logs", InspectorLogFiles.MigrateFilePathToFolder(@"C:\logs"));
        Assert.IsNull(InspectorLogFiles.MigrateFilePathToFolder(null));
        Assert.AreEqual("", InspectorLogFiles.MigrateFilePathToFolder(""));
    }

    [TestMethod]
    public void IsInspectorLogFileName_MatchesDatedAndLegacy()
    {
        Assert.IsTrue(InspectorLogFiles.IsInspectorLogFileName("titanium-inspector-2026-10-12_14-30-05.log"));
        Assert.IsTrue(InspectorLogFiles.IsInspectorLogFileName("titanium-inspector.log"));
        Assert.IsTrue(InspectorLogFiles.IsInspectorLogFileName("titanium-inspector.log.2"));
        Assert.IsFalse(InspectorLogFiles.IsInspectorLogFileName("notes.txt"));
        Assert.IsFalse(InspectorLogFiles.IsInspectorLogFileName("other.log"));
    }
}
