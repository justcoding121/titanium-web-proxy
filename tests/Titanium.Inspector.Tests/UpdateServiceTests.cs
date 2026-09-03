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
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var svc = new UpdateService(settings);
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

        var (kind, asset) = svc.ResolveAsset(manifest);
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
    public void ShouldOfferChannelInstall_SameSemverBetaBuildWhenUnknownInstall()
    {
        Assert.IsTrue(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 4), "7.0.4-beta", "Beta", null, null));
        Assert.IsTrue(UpdateService.ShouldOfferChannelInstall(
            new Version(7, 0, 4, 0), "7.0.4-beta", "Beta", null, null));
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
    }
}
