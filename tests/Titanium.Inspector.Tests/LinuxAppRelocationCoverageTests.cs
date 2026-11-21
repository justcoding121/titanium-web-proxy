using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
[SupportedOSPlatform("linux")]
public class LinuxAppRelocationCoverageTests
{
    [TestCleanup]
    public void Reset()
    {
        LinuxAppRelocation.TreatAsLinuxForTests = false;
        LinuxAppRelocation.HomeForTests = null;
        MacAppRelocation.TreatAsMacForTests = false;
        var previous = Environment.GetEnvironmentVariable("APPIMAGE");
        if (previous is not null && previous.Contains("twp-cov-appimage", StringComparison.Ordinal))
            Environment.SetEnvironmentVariable("APPIMAGE", null);
    }

    [TestMethod]
    public void AppImageInstall_CopiesIntoTheTempHome_AndOffersWhenUndismissed()
    {
        var home = Path.Combine(Path.GetTempPath(), "twp-reloc-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(home, "source");
        var appImage = Path.Combine(home, "twp-cov-appimage.AppImage");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, LinuxAppRelocation.ExeName), "payload");
        File.WriteAllText(appImage, "image");
        var previous = Environment.GetEnvironmentVariable("APPIMAGE");
        try
        {
            LinuxAppRelocation.HomeForTests = home;
            LinuxAppRelocation.TreatAsLinuxForTests = true;
            Environment.SetEnvironmentVariable("APPIMAGE", appImage);

            Assert.IsTrue(LinuxAppRelocation.IsRunningFromAppImage());
            var settings = new InspectorSettings();
            Assert.IsTrue(LinuxAppRelocation.ShouldOfferInstall(settings));

            var dest = LinuxAppRelocation.InstallToLocalShare(source, Path.Combine(home, "installed"));
            Assert.IsTrue(File.Exists(Path.Combine(dest, LinuxAppRelocation.ExeName)));
            Assert.IsTrue(File.Exists(Path.Combine(home, ".local", "bin", "titanium-inspector")));

            settings.LinuxAppImageInstallPromptDismissed = true;
            var decision = LinuxAppRelocation.DecideRelocateAction(
                runningVersion: new Version(8, 0, 5),
                installedVersion: new Version(8, 0, 4));
            Assert.AreEqual(LinuxRelocateAction.Replace, decision);

            var bundle = Path.Combine(home, "Titanium Inspector.app");
            Directory.CreateDirectory(Path.Combine(bundle, "Contents"));
            File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"),
                "<plist><key>CFBundleVersion</key><string>8.0.4</string></plist>");
            MacAppRelocation.TreatAsMacForTests = true;
            _ = MacAppRelocation.DecideRelocateAction();
            Assert.AreEqual(
                MacRelocateAction.Replace,
                MacAppRelocation.DecideRelocateAction(
                    runningVersion: new Version(9, 0, 0), installedPath: bundle));
            Assert.AreEqual(
                MacRelocateAction.OpenExisting,
                MacAppRelocation.DecideRelocateAction(
                    runningVersion: new Version(1, 0, 0), installedPath: bundle));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", previous);
            LinuxAppRelocation.TreatAsLinuxForTests = false;
            LinuxAppRelocation.HomeForTests = null;
            try { Directory.Delete(home, recursive: true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public void RelocateDecision_CoversMissingInstall_UnversionedBinary_AndNewerDll()
    {
        var home = Path.Combine(Path.GetTempPath(), "twp-reloc-dec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var previous = Environment.GetEnvironmentVariable("APPIMAGE");
        try
        {
            LinuxAppRelocation.HomeForTests = home;
            Assert.IsFalse(LinuxAppRelocation.IsRunningFromAppImage());
            LinuxAppRelocation.TreatAsLinuxForTests = true;
            Environment.SetEnvironmentVariable("APPIMAGE", null);
            Assert.IsFalse(LinuxAppRelocation.IsRunningFromAppImage());
            Assert.IsFalse(LinuxAppRelocation.ShouldOfferInstall(new InspectorSettings()));

            var image = Path.Combine(home, "twp-cov-appimage");
            File.WriteAllText(image, "image");
            Environment.SetEnvironmentVariable("APPIMAGE", image);
            Assert.IsTrue(LinuxAppRelocation.IsRunningFromAppImage());
            var dismissed = new InspectorSettings { LinuxAppImageInstallPromptDismissed = true };
            Assert.IsTrue(LinuxAppRelocation.ShouldOfferInstall(dismissed));

            Environment.SetEnvironmentVariable("APPIMAGE", null);
            var missingImage = Path.Combine(home, "missing-twp-cov-appimage");
            Environment.SetEnvironmentVariable("APPIMAGE", missingImage);
            Assert.IsFalse(LinuxAppRelocation.IsRunningFromAppImage());

            Assert.AreEqual(
                LinuxRelocateAction.Install,
                LinuxAppRelocation.DecideRelocateAction(runningVersion: new Version(8, 0, 5)));

            var installDir = Path.Combine(home, "install");
            Directory.CreateDirectory(installDir);
            var exe = Path.Combine(installDir, LinuxAppRelocation.ExeName);
            File.WriteAllText(exe, "not-a-binary");
            Assert.AreEqual(
                LinuxRelocateAction.OpenExisting,
                LinuxAppRelocation.DecideRelocateAction(
                    runningVersion: new Version(8, 0, 5), installDir: installDir));
            Assert.IsNull(LinuxAppRelocation.ReadInstalledVersion(exe));

            var dll = Path.ChangeExtension(exe, ".dll");
            File.Copy(typeof(LinuxAppRelocationCoverageTests).Assembly.Location, dll, overwrite: true);
            var fromDll = LinuxAppRelocation.ReadInstalledVersion(exe);
            Assert.IsNotNull(fromDll);
            Assert.AreEqual(
                LinuxRelocateAction.Replace,
                LinuxAppRelocation.DecideRelocateAction(runningVersion: new Version(99, 0), installDir: installDir));
            _ = LinuxAppRelocation.DecideRelocateAction();
            var copied = LinuxAppRelocation.InstallToLocalShare();
            Assert.IsTrue(Directory.Exists(copied));

            var nested = Path.Combine(home, "payload", "sub");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "note.txt"), "x");
            File.WriteAllText(Path.Combine(home, "payload", LinuxAppRelocation.ExeName), "payload");
            var dest = LinuxAppRelocation.InstallToLocalShare(Path.Combine(home, "payload"), Path.Combine(home, "out"));
            Assert.IsTrue(File.Exists(Path.Combine(dest, "sub", "note.txt")));

            Assert.ThrowsExactly<FileNotFoundException>(
                () => LinuxAppRelocation.RelaunchInstalled(Path.Combine(home, "absent")));

            var versioned = Path.Combine(home, "versioned.exe");
            File.Copy(typeof(LinuxAppRelocation).Assembly.Location, versioned, overwrite: true);
            Assert.IsNotNull(LinuxAppRelocation.ReadInstalledVersion(versioned));

            var versionDir = Path.Combine(home, "version-dir");
            Directory.CreateDirectory(versionDir);
            Assert.IsNull(LinuxAppRelocation.ReadInstalledVersion(versionDir));

            var apps = Path.Combine(home, ".local", "share", "applications");
            if (Directory.Exists(apps))
                Directory.Delete(apps, recursive: true);
            File.WriteAllText(apps, "not-a-directory");
            var blocked = LinuxAppRelocation.InstallToLocalShare(
                Path.Combine(home, "payload"),
                Path.Combine(home, "blocked-install"));
            Assert.IsTrue(Directory.Exists(blocked));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", previous);
            LinuxAppRelocation.TreatAsLinuxForTests = false;
            LinuxAppRelocation.HomeForTests = null;
            try { Directory.Delete(home, recursive: true); } catch { /* best-effort */ }
        }
    }
}
