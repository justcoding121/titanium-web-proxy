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
        StringAssert.Contains(script, "Remove-Item -LiteralPath $dest -Force");
        Assert.IsFalse(
            script.Contains("Remove-Item -LiteralPath $dest -Recurse", StringComparison.Ordinal),
            "overlay must not delete whole destination directories");
        StringAssert.Contains(script, "overlay retry");
        StringAssert.Contains(script, "apply.log");
    }

    [TestMethod]
    public void CliHelper_WindowsScript_OverlaysPerFileAndKeepsUserFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows PowerShell is required.");
        }

        var root = Path.Combine(Path.GetTempPath(), "twp-win-update-" + Guid.NewGuid().ToString("N"));
        var installDir = Path.Combine(root, "install");
        var packageDir = Path.Combine(root, "pkg");
        var zipPath = Path.Combine(root, "candidate.zip");
        var scriptPath = Path.Combine(root, "apply.ps1");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(Path.Combine(packageDir, "sub"));
        Directory.CreateDirectory(Path.Combine(packageDir, "emptydir"));
        try
        {
            File.WriteAllText(Path.Combine(installDir, "payload.txt"), "old");
            File.SetAttributes(Path.Combine(installDir, "payload.txt"), FileAttributes.ReadOnly);
            File.WriteAllText(Path.Combine(installDir, "user-keep.txt"), "keep");
            File.WriteAllText(Path.Combine(packageDir, "payload.txt"), "new");
            File.WriteAllText(Path.Combine(packageDir, "sub", "nested.txt"), "nested");
            File.WriteAllText(Path.Combine(packageDir, "titanium.exe"), "not-a-real-exe");
            ZipFile.CreateFromDirectory(packageDir, zipPath);

            var donePath = Path.Combine(root, "apply.done");
            var identityPath = Path.Combine(root, "identity", "installed-release.json");
            var script = CliUpdateApplyHelper.BuildWindowsScript(
                pid: 2147483646,
                zipPath: zipPath,
                installDir: installDir,
                relaunchPath: Path.Combine(installDir, "titanium.exe"),
                version: "99.0.0-smoke",
                channel: "beta",
                donePath: donePath,
                identityPath: identityPath);
            StringAssert.Contains(script, "'fail '");
            File.WriteAllText(scriptPath, script);
            var ps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = ps;
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(scriptPath);
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            Assert.AreEqual(0, process.ExitCode, $"apply script failed: {stderr}\n{stdout}");
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(installDir, "payload.txt")));
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(installDir, "user-keep.txt")));
            Assert.AreEqual("nested", File.ReadAllText(Path.Combine(installDir, "sub", "nested.txt")));
            Assert.IsTrue(Directory.Exists(Path.Combine(installDir, "emptydir")));
            Assert.IsTrue(File.Exists(donePath));
            var doneText = File.ReadAllText(donePath);
            StringAssert.Contains(doneText, "ok version=99.0.0-smoke");
            Assert.IsFalse(doneText.Contains("fail ", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(Path.Combine(root, "apply.log")));
            StringAssert.Contains(File.ReadAllText(identityPath), "99.0.0-smoke");
        }
        finally
        {
            if (Directory.Exists(installDir))
            {
                foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
        StringAssert.Contains(script, "chmod -R u+w \"$install_dir\"");
        StringAssert.Contains(script, "rm -f \"$target\"");
        StringAssert.Contains(script, "cp -Pp \"$f\" \"$target\"");
        StringAssert.Contains(script, "$install_dir/$rel");
        Assert.IsFalse(script.Contains("$$", StringComparison.Ordinal), "$$ is the shell pid, not the relative path");
        StringAssert.Contains(script, "\\( -type f -o -type l \\)");
        StringAssert.Contains(script, "-mindepth 1 -type d");
        Assert.IsFalse(script.Contains("cp -af", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("find \"$tmp\" -type f -print0", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("libssl", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("libcrypto", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CliHelper_UnixScript_LinuxRemovesBundledOpenSslBeforeCopy()
    {
        var script = CliUpdateApplyHelper.BuildUnixScript(
            3,
            "/tmp/cli.zip",
            "/opt/titanium",
            "/opt/titanium/titanium",
            "8.0.6",
            "stable",
            removeBundledOpenSsl: true);
        var remove = script.IndexOf("rm -f \"$install_dir\"/libssl.so* \"$install_dir\"/libcrypto.so*", StringComparison.Ordinal);
        var chmod = script.IndexOf("chmod -R u+w \"$install_dir\"", StringComparison.Ordinal);
        var copy = script.IndexOf("cp -Pp \"$f\" \"$target\"", StringComparison.Ordinal);
        var guard = script.IndexOf("compgen -G \"$install_dir\"/libmsquic.so*", StringComparison.Ordinal);
        Assert.IsTrue(guard >= 0 && remove > guard, "OpenSSL removal must be limited to a directory that contains libmsquic.");
        Assert.IsTrue(remove >= 0, "Linux update must delete bundled OpenSSL before overlaying the zip.");
        Assert.IsTrue(chmod > remove, "Install dir must be made writable after OpenSSL removal.");
        Assert.IsTrue(copy > chmod, "OpenSSL files must be removed before the new zip is copied in.");
        Assert.IsFalse(script.Contains("libssl.3.dylib", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CliHelper_UnixScript_OverwritesReadOnlyDestinationFiles()
    {
        // Reproduces the macOS zip mode trap (Homebrew dylibs at 0444) on Linux
        // with GNU cp: plain `cp -a` fails; the fixed remove-then-copy script must succeed.
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("bash, chmod, and zip are required.");
        }

        if (!File.Exists("/bin/bash") && !File.Exists("/usr/bin/bash"))
        {
            Assert.Fail("bash is required on Linux/macOS CI for the read-only overlay proof.");
        }

        var root = Path.Combine(Path.GetTempPath(), "twp-ro-update-" + Guid.NewGuid().ToString("N"));
        var installDir = Path.Combine(root, "install");
        var packageDir = Path.Combine(root, "pkg");
        var zipPath = Path.Combine(root, "candidate.zip");
        var scriptPath = Path.Combine(root, "apply.sh");
        var relaunch = Path.Combine(installDir, "titanium");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(packageDir);
        try
        {
            var destPayload = Path.Combine(installDir, "payload.txt");
            File.WriteAllText(destPayload, "old");
            RunBash($"chmod 444 '{destPayload.Replace("'", "'\\''", StringComparison.Ordinal)}'");

            File.WriteAllText(Path.Combine(packageDir, "payload.txt"), "new");
            File.WriteAllText(Path.Combine(packageDir, "titanium"), "#!/bin/sh\necho ok\n");
            Directory.CreateDirectory(Path.Combine(packageDir, "emptydir"));
            File.WriteAllText(Path.Combine(installDir, "user-keep.txt"), "keep");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            // ZipFile stores the empty directory. `zip -y` symlinks make Info-ZIP
            // unzip exit 1 ("mkdir: Already exists") on the CI image, so the
            // symlink copy is asserted from the generated script text.
            ZipFile.CreateFromDirectory(packageDir, zipPath);

            var donePath = Path.Combine(root, "apply.done");
            var identityPath = Path.Combine(root, "identity", "installed-release.json");
            // pid 1 stays alive on Unix; use a definitely-dead pid so the wait loop exits.
            var script = CliUpdateApplyHelper.BuildUnixScript(
                pid: 2147483646,
                zipPath: zipPath,
                installDir: installDir,
                relaunchPath: relaunch,
                version: "99.0.0-smoke",
                channel: "beta",
                removeBundledOpenSsl: false,
                donePath: donePath,
                identityPath: identityPath);
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            RunBash($"chmod +x '{scriptPath.Replace("'", "'\\''", StringComparison.Ordinal)}' && bash '{scriptPath.Replace("'", "'\\''", StringComparison.Ordinal)}'");

            Assert.AreEqual("new", File.ReadAllText(destPayload));
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(installDir, "user-keep.txt")));
            Assert.IsTrue(Directory.Exists(Path.Combine(installDir, "emptydir")), "empty directories in the zip must be created");
            Assert.IsTrue(File.Exists(donePath), "apply.done must be written after a successful overlay");
            StringAssert.Contains(File.ReadAllText(donePath), "ok version=99.0.0-smoke");
            Assert.IsTrue(File.Exists(identityPath), "identity must be written after overlay, not before");
            StringAssert.Contains(File.ReadAllText(identityPath), "99.0.0-smoke");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    RunBash($"chmod -R u+w '{root.Replace("'", "'\\''", StringComparison.Ordinal)}' || true");
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of 0444 trees.
            }
        }
    }

    private static void RunBash(string command)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add("-lc");
        process.StartInfo.ArgumentList.Add(command);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        Assert.AreEqual(0, process.ExitCode, $"bash failed: {stderr}\n{stdout}");
    }

    [TestMethod]
    public void InstallDirectoryIsWritable_RejectsAPathBlockedByAFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-nowrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.IsTrue(UpdateCommand.InstallDirectoryIsWritable(root));
            var blocker = Path.Combine(root, "blocked");
            File.WriteAllText(blocker, "not a directory");
            Assert.IsFalse(
                UpdateCommand.InstallDirectoryIsWritable(Path.Combine(blocker, "install")),
                UpdateCommand.InstallDirNotWritableMessage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        Assert.ThrowsExactly<InvalidDataException>(() => UpdateCommand.EnsurePlusBundleInventory(tooMany));
        Assert.ThrowsExactly<InvalidDataException>(() => UpdateCommand.EnsurePlusBundleInventory(
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
            Assert.AreEqual(0, Directory.GetDirectories(install, ".plus-stage-*").Length);
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
    public void PlusBundle_RejectsZipSlipParentEntry()
    {
        var root = NewTempDir();
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        try
        {
            var zipPath = Path.Combine(root, "slip.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../evil.dll");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("nope");
            }

            Assert.AreEqual(1, UpdateCommand.ApplyPlusBundleZip(zipPath, install));
            Assert.IsFalse(File.Exists(Path.Combine(root, "evil.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(install, "evil.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(install, "Titanium.Plus.dll")));
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
