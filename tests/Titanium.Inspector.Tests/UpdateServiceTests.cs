using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class UpdateServiceTests
{
    [TestMethod]
    public void SuggestRid_ReturnsNonEmpty()
    {
        var rid = UpdateService.SuggestRid();
        Assert.IsFalse(string.IsNullOrWhiteSpace(rid));
        StringAssert.Contains(rid, "-");
    }

    [TestMethod]
    public void ResolveAsset_UsesRidZip_WhenMsiAssetAbsent()
    {
        var rid = UpdateService.SuggestRid();
        var manifest = new InspectorReleaseManifest
        {
            Version = "9.9.9",
            Products = new InspectorProductsBlock
            {
                Inspector = new InspectorProductAssets
                {
                    Assets = new Dictionary<string, ManifestAsset>
                    {
                        [rid] = new ManifestAsset
                        {
                            Url = "https://example.test/rid.zip",
                            Sha256 = "cc",
                        },
                    },
                },
            },
        };

        var (kind, asset) = UpdateService.ResolveAsset(manifest);
        Assert.IsNotNull(asset);
        Assert.AreEqual(UpdateApplyKind.Zip, kind);
        Assert.AreEqual("https://example.test/rid.zip", asset!.Url);
    }

    [TestMethod]
    public void IsMsiInstall_DetectsProgramFilesPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.IsTrue(UpdateService.IsMsiInstall(Path.Combine(pf, "Titanium Inspector")));
    }

    [TestMethod]
    public void BuildWindowsScript_ContainsMsiexec_ForMsi()
    {
        var script = UpdateApplyHelper.BuildWindowsScript(
            42,
            UpdateApplyKind.Msi,
            @"C:\temp\a.msi",
            @"C:\Program Files\Titanium Inspector",
            @"C:\Program Files\Titanium Inspector\TitaniumInspector.exe",
            "7.0.4",
            "Stable");
        StringAssert.Contains(script, "msiexec");
        StringAssert.Contains(script, "42");
        StringAssert.Contains(script, "7.0.4");
        StringAssert.Contains(script, "Stable");
    }

    [TestMethod]
    public void BuildUnixScript_UnzipsAndRelaunches()
    {
        var script = UpdateApplyHelper.BuildUnixScript(
            99,
            "/tmp/a.zip",
            "/opt/ti",
            "/opt/ti/TitaniumInspector",
            "7.0.4-beta",
            "Beta");
        StringAssert.Contains(script, "unzip");
        StringAssert.Contains(script, "99");
        StringAssert.Contains(script, "Beta");
        StringAssert.Contains(script, "cp -Pp \"$f\" \"$target\"");
        Assert.IsFalse(script.Contains("cp -a", StringComparison.Ordinal));
        // Non-.app relaunch uses nohup; .app path is the other branch in the same script.
        StringAssert.Contains(script, "nohup");
        var remove = script.IndexOf("rm -f \"$install_dir\"/libssl.so* \"$install_dir\"/libcrypto.so*", StringComparison.Ordinal);
        var copy = script.LastIndexOf("cp -Pp \"$f\" \"$target\"", StringComparison.Ordinal);
        var guard = script.IndexOf("compgen -G \"$install_dir\"/libmsquic.so*", StringComparison.Ordinal);
        Assert.IsTrue(guard >= 0 && remove > guard, "OpenSSL removal must be limited to a directory that contains libmsquic.");
        Assert.IsTrue(remove >= 0 && copy > remove, "Linux zip apply must drop bundled OpenSSL before the overlay.");
        Assert.IsTrue(script.IndexOf("ditto", StringComparison.Ordinal) < script.IndexOf("else", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildUnixScript_MacApp_UsesDittoIntoContentsMacOS()
    {
        var script = UpdateApplyHelper.BuildUnixScript(
            42,
            "/tmp/a.zip",
            "/Applications/Titanium Inspector.app/Contents/MacOS",
            "/Applications/Titanium Inspector.app",
            "7.1.0-beta",
            "Stable");
        StringAssert.Contains(script, "unzip");
        StringAssert.Contains(script, "ditto");
        StringAssert.Contains(script, "Contents/MacOS");
        StringAssert.Contains(script, "open");
        StringAssert.Contains(script, "PlistBuddy");
        StringAssert.Contains(script, "CFBundleShortVersionString");
        StringAssert.Contains(script, "CFBundleVersion");
        // Packaging strips the prerelease suffix for Info.plist.
        StringAssert.Contains(script, "plist_version='7.1.0'");
        Assert.AreEqual("7.1.0", UpdateApplyHelper.StripPrereleaseSuffix("7.1.0-beta"));
    }

    [TestMethod]
    public void RefuseApplyReason_WhenNotOnDmgOrAppImage_IsNull()
    {
        // Unit-test host is not a DMG/AppImage mount.
        Assert.IsNull(UpdateService.RefuseApplyReason());
    }

    [TestMethod]
    public void BuildUnixScript_DebUsesPackageManager()
    {
        var script = UpdateApplyHelper.BuildUnixScript(
            7,
            "/tmp/TitaniumInspector.deb",
            "/opt/titanium-inspector",
            "/opt/titanium-inspector/TitaniumInspector",
            "8.0.4",
            "Stable",
            UpdateApplyKind.Deb);
        StringAssert.Contains(script, "pkexec dpkg -i");
        StringAssert.Contains(script, "sudo -n dpkg -i");
        StringAssert.Contains(script, "nohup \"$relaunch\"");
        Assert.IsFalse(script.Contains("settings.json", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("unzip", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DetectLinuxPackageKind_NonOptDirectory_IsZip()
    {
        var previous = Environment.GetEnvironmentVariable("APPIMAGE");
        Environment.SetEnvironmentVariable("APPIMAGE", null);
        try
        {
            Assert.AreEqual(UpdateApplyKind.Zip, UpdateService.DetectLinuxPackageKind(Path.GetTempPath()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", previous);
        }
    }

    [TestMethod]
    public void DetectLinuxPackageKind_AppImageEnv_IsAppImage()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var previous = Environment.GetEnvironmentVariable("APPIMAGE");
        Environment.SetEnvironmentVariable("APPIMAGE", "/tmp/TitaniumInspector.AppImage");
        try
        {
            Assert.AreEqual(UpdateApplyKind.AppImage, UpdateService.DetectLinuxPackageKind(Path.GetTempPath()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", previous);
        }
    }

    [TestMethod]
    public void BuildUnixScript_AppImageReplacesFile()
    {
        var script = UpdateApplyHelper.BuildUnixScript(
            9,
            "/tmp/TitaniumInspector-9.0.0.AppImage",
            "/home/user/Downloads",
            "/home/user/Downloads/TitaniumInspector.AppImage",
            "9.0.0",
            "Stable",
            UpdateApplyKind.AppImage);
        StringAssert.Contains(script, "cp -f \"$package\" \"$relaunch\"");
        Assert.IsFalse(script.Contains("unzip", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResolveAsset_AppImage_PrefersAppImageKey()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var previous = Environment.GetEnvironmentVariable("APPIMAGE");
        Environment.SetEnvironmentVariable("APPIMAGE", "/tmp/TitaniumInspector.AppImage");
        try
        {
            var rid = UpdateService.SuggestRid();
            var manifest = new InspectorReleaseManifest
            {
                Version = "9.9.9",
                Products = new InspectorProductsBlock
                {
                    Inspector = new InspectorProductAssets
                    {
                        Assets = new Dictionary<string, ManifestAsset>
                        {
                            [rid] = new ManifestAsset { Url = "https://example.test/rid.zip", Sha256 = "aa" },
                            [rid + ".AppImage"] = new ManifestAsset { Url = "https://example.test/app.AppImage", Sha256 = "bb" },
                        },
                    },
                },
            };

            var (kind, asset) = UpdateService.ResolveAsset(manifest);
            Assert.AreEqual(UpdateApplyKind.AppImage, kind);
            Assert.AreEqual("https://example.test/app.AppImage", asset!.Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", previous);
        }
    }

    [TestMethod]
    public void Sha256_MismatchDetection_MatchesHex()
    {
        var bytes = Encoding.UTF8.GetBytes("hello");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.AreEqual(64, hash.Length);
        Assert.AreNotEqual(hash, Convert.ToHexString(SHA256.HashData("other"u8.ToArray())).ToLowerInvariant());
    }

    [TestMethod]
    public async Task ScriptedDialog_ConfirmInstallUpdate_RecordsChannel()
    {
        var dialogs = new ScriptedInspectorDialogs { InstallUpdateResult = true };
        var ok = await dialogs.ConfirmInstallUpdateAsync(null, "7.1.0", "Beta", UpdateOfferKind.Upgrade);
        Assert.IsTrue(ok);
        Assert.AreEqual(1, dialogs.InstallUpdateCalls);
        Assert.AreEqual("7.1.0", dialogs.LastInstallUpdateVersion);
        Assert.AreEqual("Beta", dialogs.LastInstallUpdateChannel);
        Assert.AreEqual(UpdateOfferKind.Upgrade, dialogs.LastInstallUpdateOfferKind);
    }

    [TestMethod]
    public void ShouldOfferChannelInstall_UpgradeWhenRemoteNewer()
    {
        Assert.IsTrue(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 3), "7.0.4", "Stable", null, null));
        Assert.IsTrue(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 3, 0), "7.0.4", "Stable", null, null));
    }

    [TestMethod]
    public void ShouldOfferChannelInstall_DowngradeWhenSwitchingToStable()
    {
        Assert.IsTrue(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 4), "7.0.3", "Stable", "7.0.4-beta", "Beta"));
    }

    [TestMethod]
    public void ShouldOfferChannelInstall_SameSemverBetaOverStable_DoesNotOffer()
    {
        // Stable-looking local (no prerelease identity) must not be offered same-core beta.
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 5), "7.0.5-beta", "Beta", null, null));
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 5, 0), "7.0.5-beta", "Beta", null, null));
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 5, 0), "7.0.5-beta", "Beta", "7.0.5", "Stable"));
    }

    [TestMethod]
    public void ShouldOfferChannelInstall_NewerBetaOverStable_OffersUpgrade()
    {
        Assert.IsTrue(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 5, 0), "7.0.6-beta", "Beta", null, null));
        Assert.IsTrue(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 5, 0), "7.0.6-beta", "Beta", "7.0.5", "Stable"));
    }

    [TestMethod]
    public void ShouldOfferChannelInstall_UpToDateWhenTagAndChannelMatch()
    {
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 4), "7.0.4-beta", "Beta", "7.0.4-beta", "Beta"));
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 4), "7.0.4", "Stable", "7.0.4", "Stable"));
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 4, 0), "7.0.4", "Stable", "7.0.4", "Stable"));
    }

    [TestMethod]
    public void ShouldOfferChannelInstall_UpToDateStableWhenUnknownInstallAndSameSemver()
    {
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 4), "7.0.4", "Stable", null, null));
        // Assembly versions are 4-part; feed tags are 3-part — must not false-offer.
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 5, 0), "7.0.5", "Stable", null, null));
    }

    [TestMethod]
    public void ShouldOfferChannelInstall_UnknownOriginOlderRemote_DoesNotOffer()
    {
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 5, 0), "7.0.4", "Stable", null, null));
    }

    [TestMethod]
    public void ShouldOfferChannelInstall_ChannelSwitchAtSameSemverWhenTagKnown()
    {
        Assert.IsTrue(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 4), "7.0.4", "Stable", "7.0.4-beta", "Beta"));
        Assert.IsTrue(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 4, 0), "7.0.4", "Stable", "7.0.4-beta", "Beta"));
    }

    [TestMethod]
    public void ShouldOfferChannelInstall_UpToDateWhenInformationalMatchesRemote()
    {
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 5, 0), "7.0.5-beta", "Beta", null, null, "7.0.5-beta"));
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 5, 0), "7.0.5-beta", "Beta", null, null, "v7.0.5-beta"));
        // Without informational identity, assembly-only Stable-looking installs are not offered same-core beta.
        Assert.IsFalse(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 5, 0), "7.0.5-beta", "Beta", null, null));
    }

    [TestMethod]
    public void FormatNoOfferMessage_DistinguishesNoNewerBeta()
    {
        StringAssert.Contains(
            UpdateService.FormatNoOfferMessage("7.0.5", "7.0.5-beta", "Beta"),
            "No newer Beta");
        Assert.AreEqual(
            "Titanium Inspector is up to date (Stable).",
            UpdateService.FormatNoOfferMessage("7.0.5", "7.0.5", "Stable"));
    }

    [TestMethod]
    public void ShouldSeedInstalledIdentity_OnlyWhenRemoteIsLocalBuild()
    {
        Assert.IsTrue(UpdateService.ShouldSeedInstalledIdentity("7.0.5-beta", null, "7.0.5-beta"));
        Assert.IsTrue(UpdateService.ShouldSeedInstalledIdentity(null, "7.0.5-beta", "7.0.5-beta"));
        Assert.IsFalse(UpdateService.ShouldSeedInstalledIdentity(null, null, "7.0.5-beta"));
        Assert.IsFalse(UpdateService.ShouldSeedInstalledIdentity(null, "7.0.5", "7.0.5-beta"));
    }

    [TestMethod]
    public void Save_KeepsDecryptHttpsWhenTheInstalledReleaseChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-settings-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.DecryptHttps = true;
            settings.Current.InstalledReleaseTag = "8.0.4";
            settings.Save();

            settings.Current.InstalledReleaseTag = "8.0.6";
            settings.Current.InstalledReleaseChannel = "Stable";
            settings.Save();

            var reloaded = new SettingsService(path);
            Assert.IsTrue(reloaded.Current.DecryptHttps);
            Assert.AreEqual("8.0.6", reloaded.Current.InstalledReleaseTag);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void MsiOfferIsDowngrade_OnlyLowerProductVersion()
    {
        var v704 = new Version(7, 0, 4);
        var v705 = new Version(7, 0, 5);
        Assert.IsFalse(UpdateService.MsiOfferIsDowngrade(v705, v705));
        Assert.IsFalse(UpdateService.MsiOfferIsDowngrade(v704, v705));
        Assert.IsTrue(UpdateService.MsiOfferIsDowngrade(v705, v704));
    }

    [TestMethod]
    public void FormatInformationalVersion_StripsSourceLinkAndEmpty()
    {
        Assert.AreEqual("7.0.5-beta", UpdateService.FormatInformationalVersion("7.0.5-beta+abcdef"));
        Assert.AreEqual("7.0.5", UpdateService.FormatInformationalVersion("7.0.5"));
        Assert.IsNull(UpdateService.FormatInformationalVersion(null));
        Assert.IsNull(UpdateService.FormatInformationalVersion(""));
        Assert.IsNull(UpdateService.FormatInformationalVersion("   "));
        Assert.IsNull(UpdateService.FormatInformationalVersion("+onlymeta"));
    }

    [TestMethod]
    public void ClassifyOfferKind_DistinguishesUpgradeChannelAndDowngrade()
    {
        Assert.AreEqual(
            UpdateOfferKind.Upgrade,
            UpdateService.ClassifyOfferKind(new Version(7, 0, 4, 0), "7.0.5", "Stable", null, null));
        Assert.AreEqual(
            UpdateOfferKind.ChannelSwitch,
            UpdateService.ClassifyOfferKind(
                new Version(7, 0, 4, 0), "7.0.4", "Stable", "7.0.4-beta", "Beta"));
        Assert.AreEqual(
            UpdateOfferKind.Downgrade,
            UpdateService.ClassifyOfferKind(
                new Version(7, 0, 5, 0), "7.0.4", "Stable", "7.0.5-beta", "Beta"));
        Assert.AreEqual(
            UpdateOfferKind.None,
            UpdateService.ClassifyOfferKind(new Version(7, 0, 5, 0), "7.0.5", "Stable", null, null));
        Assert.AreEqual(
            UpdateOfferKind.None,
            UpdateService.ClassifyOfferKind(new Version(7, 0, 5, 0), "7.0.5-beta", "Beta", null, null));
        Assert.AreEqual(
            UpdateOfferKind.Upgrade,
            UpdateService.ClassifyOfferKind(new Version(7, 0, 5, 0), "7.0.6-beta", "Beta", null, null));
    }

    [TestMethod]
    public void ClassifyOfferKind_SmokeInAppApply_RemoteAboveAssemblyOffersUpgrade()
    {
        // Linux/macOS FileVersionInfo often returns 0.0.0; the harness then serves a high
        // sentinel so InformationalVersion 8.0.9 still sees an Upgrade (not a downgrade).
        Assert.AreEqual(
            UpdateOfferKind.None,
            UpdateService.ClassifyOfferKind(
                new Version(8, 0, 9, 0),
                "0.0.1-smoke-in-app",
                "Stable",
                "0.0.0",
                "Stable",
                localInformationalVersion: "8.0.9"));
        Assert.AreEqual(
            UpdateOfferKind.Upgrade,
            UpdateService.ClassifyOfferKind(
                new Version(8, 0, 9, 0),
                "99.0.0-smoke-in-app",
                "Stable",
                "0.0.0",
                "Stable",
                localInformationalVersion: "8.0.9"));
        Assert.AreEqual(
            UpdateOfferKind.Upgrade,
            UpdateService.ClassifyOfferKind(
                new Version(8, 0, 9, 0),
                "8.0.10-smoke-in-app",
                "Stable",
                "8.0.9",
                "Stable",
                localInformationalVersion: "8.0.9"));
    }

    [TestMethod]
    public void ShouldAutoApplyUpdate_RequiresLoopbackHttpFeed()
    {
        var prevApply = Environment.GetEnvironmentVariable("TITANIUM_UPDATE_AUTO_APPLY");
        var prevFeed = Environment.GetEnvironmentVariable("TITANIUM_UPDATE_FEED");
        try
        {
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_AUTO_APPLY", "1");
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", "http://127.0.0.1:18700/release-manifest.json");
            Assert.IsTrue(UpdateService.ShouldAutoApplyUpdate());

            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", "https://example.com/release-manifest.json");
            Assert.IsFalse(UpdateService.ShouldAutoApplyUpdate());

            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", "http://updates.example.com/release-manifest.json");
            Assert.IsFalse(UpdateService.ShouldAutoApplyUpdate());

            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_AUTO_APPLY", null);
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", "http://127.0.0.1:18700/release-manifest.json");
            Assert.IsFalse(UpdateService.ShouldAutoApplyUpdate());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_AUTO_APPLY", prevApply);
            Environment.SetEnvironmentVariable("TITANIUM_UPDATE_FEED", prevFeed);
        }
    }
}
