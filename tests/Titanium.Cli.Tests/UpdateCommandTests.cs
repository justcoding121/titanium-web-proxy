using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Updates;
using Titanium.Web.Proxy.Abstractions.Updates;

namespace Titanium.Cli.Tests;

[TestClass]
public class UpdateCommandTests
{
    [TestMethod]
    public void ParseChannel_DefaultsStable()
    {
        Assert.AreEqual("stable", VersionCommand.ParseChannel([]));
    }

    [TestMethod]
    public void ParseChannel_ReadsFlag()
    {
        Assert.AreEqual("beta", VersionCommand.ParseChannel(["--channel", "Beta"]));
    }

    [TestMethod]
    public void TryResolveChannel_RejectsUnknown()
    {
        Assert.IsFalse(VersionCommand.TryResolveChannel(["--channel", "nightly"], out _, out var error));
        StringAssert.Contains(error, "Unknown channel");
    }

    [TestMethod]
    public void StripPrerelease_RemovesSuffix()
    {
        Assert.AreEqual("7.0.4", VersionCommand.StripPrerelease("v7.0.4-beta"));
        Assert.AreEqual("7.0.4", VersionCommand.StripPrerelease("7.0.4"));
    }

    [TestMethod]
    public void ReleaseVersion_ThreePartEqualsFourPartAssembly()
    {
        Assert.AreEqual(0, ReleaseVersion.Compare(new Version(7, 0, 5, 0), ReleaseVersion.ParseComparable("7.0.5")));
        Assert.AreEqual("7.0.5", ReleaseVersion.FormatDisplay(new Version(7, 0, 5, 0)));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_SameSemverThreeVsFourPart_IsUpToDate()
    {
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5", "stable", "7.0.5", "stable"));
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5", "stable", null, null));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_UpgradeWhenRemoteNewer()
    {
        Assert.IsTrue(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.6", "stable", null, null));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_LocalNewer_DoesNotInstall()
    {
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 6, 0), "7.0.5", "stable", null, null));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_SameSemverBetaOverStable_DoesNotInstall()
    {
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5-beta", "beta", "7.0.5", "stable"));
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5-beta", "beta", null, null));
        Assert.IsFalse(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5-beta", "beta", "7.0.5-beta", "beta"));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_NewerBetaOverStable_Installs()
    {
        Assert.IsTrue(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.6-beta", "beta", "7.0.5", "stable"));
    }

    [TestMethod]
    public void ShouldInstallCliRelease_SameSemverStableOverBeta_Installs()
    {
        Assert.IsTrue(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5", "stable", "7.0.5-beta", "beta"));
        Assert.IsTrue(VersionCommand.ShouldInstallCliRelease(
            new Version(7, 0, 5, 0), "7.0.5", "stable", null, null, "7.0.5-beta"));
    }

    [TestMethod]
    public void ShouldInstallPlusRelease_SkipsEqualAndOlder_InstallsNewer()
    {
        Assert.IsTrue(VersionCommand.ShouldInstallPlusRelease(null, "7.0.5-beta"));
        Assert.IsFalse(VersionCommand.ShouldInstallPlusRelease(new Version(7, 0, 5, 0), "7.0.5-beta"));
        Assert.IsFalse(VersionCommand.ShouldInstallPlusRelease(new Version(7, 0, 6, 0), "7.0.5"));
        Assert.IsTrue(VersionCommand.ShouldInstallPlusRelease(new Version(7, 0, 5, 0), "7.0.6-beta"));
        Assert.IsTrue(VersionCommand.ShouldInstallPlusRelease(
            new Version(7, 0, 5, 0), "7.0.5", "7.0.5-beta"));
    }

    [TestMethod]
    public void ReleaseVersion_CompareReleaseTags_ReleaseBeatsSameCorePrerelease()
    {
        Assert.IsTrue(ReleaseVersion.CompareReleaseTags("7.0.5-beta", "7.0.5") < 0);
        Assert.IsTrue(ReleaseVersion.CompareReleaseTags("7.0.5", "7.0.5-beta") > 0);
        Assert.AreEqual(0, ReleaseVersion.CompareReleaseTags("7.0.5", "v7.0.5"));
        Assert.IsTrue(ReleaseVersion.IsRemoteNewer("7.0.5", "7.0.6-beta"));
        Assert.IsFalse(ReleaseVersion.IsRemoteNewer("7.0.5", "7.0.5-beta"));
        Assert.IsTrue(ReleaseVersion.IsPrereleaseTag("7.0.5-beta"));
        Assert.IsFalse(ReleaseVersion.IsPrereleaseTag("7.0.5"));
        Assert.AreEqual(
            "7.0.5-beta",
            ReleaseVersion.ResolveLocalReleaseLabel(new Version(7, 0, 5, 0), "7.0.5-beta+abc", null));
        Assert.AreEqual(
            "7.0.5",
            ReleaseVersion.ResolveLocalReleaseLabel(new Version(7, 0, 5, 0), null, null));
    }

    [TestMethod]
    public void CliHelper_WindowsScript_ExtractsZip()
    {
        var script = CliUpdateApplyHelper.BuildWindowsScript(
            7,
            @"C:\temp\cli.zip",
            @"C:\tools\titanium",
            @"C:\tools\titanium\titanium.exe",
            "7.0.4",
            "stable");
        StringAssert.Contains(script, "Expand-Archive");
        StringAssert.Contains(script, "7");
        StringAssert.Contains(script, "stable");
        StringAssert.Contains(script, "version");
    }

    [TestMethod]
    public void CliHelper_UnixScript_Unzips()
    {
        var script = CliUpdateApplyHelper.BuildUnixScript(
            3,
            "/tmp/cli.zip",
            "/opt/titanium",
            "/opt/titanium/titanium",
            "7.0.4-beta",
            "beta");
        StringAssert.Contains(script, "unzip");
        StringAssert.Contains(script, "beta");
    }

    [TestMethod]
    public void RemovePlus_DeletesDllBakAndStaging()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-remove-plus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dll = Path.Combine(dir, "Titanium.Plus.dll");
            File.WriteAllText(dll, "plus");
            File.WriteAllText(dll + ".bak", "bak");
            File.WriteAllText(dll + ".new", "new");

            Assert.AreEqual(0, UpdateCommand.RemovePlus(dir));
            Assert.IsFalse(File.Exists(dll));
            Assert.IsFalse(File.Exists(dll + ".bak"));
            Assert.IsFalse(File.Exists(dll + ".new"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void RemovePlus_IdempotentWhenAbsent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-remove-plus-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.AreEqual(0, UpdateCommand.RemovePlus(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task Update_RejectsPlusAndRemovePlusTogether()
    {
        var code = await UpdateCommand.ExecuteAsync(["update", "--plus", "--remove-plus"]);
        Assert.AreEqual(1, code);
    }

    [TestMethod]
    public void UpdateFeedSchema_PlusKeepsAssetAndAddsBundle()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Titanium.Web.Proxy.sln")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir);
        var schema = File.ReadAllText(Path.Combine(dir.FullName, "tools", "packaging", "update-feed.schema.json"));
        StringAssert.Contains(schema, "\"asset\"");
        StringAssert.Contains(schema, "\"bundle\"");
        StringAssert.Contains(schema, "\"size\"");
    }

    [TestMethod]
    public void PlusBundle_RejectsUnsafeNamesAndOversizeInventory()
    {
        Assert.IsFalse(UpdateCommand.IsPlainPlusFileName("../x.dll"));
        Assert.IsFalse(UpdateCommand.IsPlainPlusFileName("..\\x.dll"));
        Assert.IsFalse(UpdateCommand.IsPlainPlusFileName("nested/x.dll"));
        Assert.IsFalse(UpdateCommand.IsPlainPlusFileName("nested\\x.dll"));
        Assert.IsFalse(UpdateCommand.IsPlainPlusFileName("C:x.dll"));
        Assert.IsTrue(UpdateCommand.IsPlainPlusFileName("Titanium.Plus.dll"));

        var tooMany = Enumerable.Range(0, UpdateCommand.MaxPlusBundleEntries + 1)
            .Select(i => ($"f{i}.dll", 1L))
            .ToList();
        Assert.ThrowsException<InvalidDataException>(() => UpdateCommand.EnsurePlusBundleInventory(tooMany));
        Assert.ThrowsException<InvalidDataException>(() => UpdateCommand.EnsurePlusBundleInventory(
            [("a.dll", UpdateCommand.MaxPlusBundleUncompressedBytes + 1)]));
    }

    [TestMethod]
    public void PlusBundle_ApplyAndRemove_RestoresCliFile()
    {
        var root = NewTempDir();
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        try
        {
            File.WriteAllText(Path.Combine(install, "titanium.dll"), "cli");
            var zip = WriteBundle(root, new Dictionary<string, string>
            {
                ["Titanium.Plus.dll"] = "plus",
                ["StackExchange.Redis.dll"] = "redis",
            });

            Assert.AreEqual(0, UpdateCommand.ApplyPlusBundleZip(zip, install));
            Assert.AreEqual("plus", File.ReadAllText(Path.Combine(install, "Titanium.Plus.dll")));
            Assert.AreEqual("redis", File.ReadAllText(Path.Combine(install, "StackExchange.Redis.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(install, UpdateCommand.PlusFileListName)));
            Assert.AreEqual("cli", File.ReadAllText(Path.Combine(install, "titanium.dll")));

            Assert.AreEqual(0, UpdateCommand.RemovePlus(install));
            Assert.IsFalse(File.Exists(Path.Combine(install, "Titanium.Plus.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(install, "StackExchange.Redis.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(install, UpdateCommand.PlusFileListName)));
            Assert.AreEqual("cli", File.ReadAllText(Path.Combine(install, "titanium.dll")));
            Assert.AreEqual(0, Directory.GetDirectories(root, ".plus-stage-*").Length);
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    [TestMethod]
    public void PlusBundle_RejectsNestedEntry()
    {
        var root = NewTempDir();
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        try
        {
            var zipPath = Path.Combine(root, "bad.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("nested/Titanium.Plus.dll");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("nope");
            }

            Assert.AreEqual(1, UpdateCommand.ApplyPlusBundleZip(zipPath, install));
            Assert.IsFalse(File.Exists(Path.Combine(install, "Titanium.Plus.dll")));
            Assert.IsFalse(Directory.Exists(Path.Combine(install, "nested")));
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    [TestMethod]
    public void PlusBundle_RollsBackWhenALaterFileFails()
    {
        var root = NewTempDir();
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        try
        {
            File.WriteAllText(Path.Combine(install, "keep.txt"), "cli");
            File.WriteAllText(Path.Combine(install, "Titanium.Plus.dll"), "old");
            // A directory where a bundle file should land makes the swap fail after earlier files move.
            Directory.CreateDirectory(Path.Combine(install, "Zed.dll"));
            var zip = WriteBundle(root, new Dictionary<string, string>
            {
                ["Titanium.Plus.dll"] = "new",
                ["Zed.dll"] = "x",
            });

            Assert.AreEqual(1, UpdateCommand.ApplyPlusBundleZip(zip, install));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "Titanium.Plus.dll")));
            Assert.AreEqual("cli", File.ReadAllText(Path.Combine(install, "keep.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(install, UpdateCommand.PlusFileListName)));
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    [TestMethod]
    public void RemovePlus_DeletesListedFilesAndLeavesCliFiles()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "titanium.dll"), "cli");
            File.WriteAllText(Path.Combine(dir, "Titanium.Plus.dll"), "plus");
            File.WriteAllText(Path.Combine(dir, "StackExchange.Redis.dll"), "redis");
            File.WriteAllLines(
                Path.Combine(dir, UpdateCommand.PlusFileListName),
                ["StackExchange.Redis.dll", "Titanium.Plus.dll", UpdateCommand.PlusFileListName]);

            Assert.AreEqual(0, UpdateCommand.RemovePlus(dir));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "Titanium.Plus.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "StackExchange.Redis.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(dir, UpdateCommand.PlusFileListName)));
            Assert.AreEqual("cli", File.ReadAllText(Path.Combine(dir, "titanium.dll")));
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    [TestMethod]
    public void RemovePlus_RefusesUnsafeListWithoutDeleting()
    {
        var dir = NewTempDir();
        try
        {
            var plus = Path.Combine(dir, "Titanium.Plus.dll");
            File.WriteAllText(plus, "plus");
            File.WriteAllText(Path.Combine(dir, UpdateCommand.PlusFileListName), "../evil.dll");

            Assert.AreEqual(1, UpdateCommand.RemovePlus(dir));
            Assert.AreEqual("plus", File.ReadAllText(plus));
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    private static string WriteBundle(string directory, IReadOnlyDictionary<string, string> files)
    {
        var names = files.Keys.Append(UpdateCommand.PlusFileListName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var zipPath = Path.Combine(directory, "bundle.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var name in names)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (name == UpdateCommand.PlusFileListName)
            {
                foreach (var line in names)
                {
                    writer.WriteLine(line);
                }
            }
            else
            {
                writer.Write(files[name]);
            }
        }

        return zipPath;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-plus-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteTree(string dir)
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
            // ignore
        }
    }
}
