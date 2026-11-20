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
}
