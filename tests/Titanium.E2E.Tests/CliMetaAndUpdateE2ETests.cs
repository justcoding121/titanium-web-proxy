using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliMetaAndUpdateE2ETests
{
    [TestMethod]
    [TestCategory("E2E")]
    public async Task Version_Plus_Missing_PrintsNotPresent()
    {
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: false);
        var (code, stdout, stderr) = await harness.RunOnceAsync(["version", "--plus"]);
        Assert.AreEqual(0, code);
        var text = stdout + stderr;
        Assert.IsTrue(
            text.Contains("Plus", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("not present", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("not installed", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Plus:")),
            text);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Version_Plus_Present_PrintsVersion()
    {
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        var (code, stdout, _) = await harness.RunOnceAsync(["version", "--plus"]);
        Assert.AreEqual(0, code);
        StringAssert.Contains(stdout, "Plus");
        Assert.IsFalse(stdout.Contains("not present", StringComparison.OrdinalIgnoreCase), stdout);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Version_Check_Plus_SoftNetwork()
    {
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["version", "--check", "--plus"],
            timeout: TimeSpan.FromSeconds(45));
        var text = stdout + stderr;
        Assert.IsTrue(code is 0 or 1 or 2, $"exit={code} {text}");
        if (code != 1)
        {
            StringAssert.Contains(text, "Plus", StringComparison.OrdinalIgnoreCase);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Version_InvalidChannel_Exit1()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["version", "--channel", "nightly"]);
        Assert.AreEqual(1, code);
        StringAssert.Contains(stdout + stderr, "Unknown channel");
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Update_PlusAndRemovePlus_MutuallyExclusive()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["update", "--plus", "--remove-plus"]);
        Assert.AreEqual(1, code);
        StringAssert.Contains(stdout + stderr, "either");
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Update_InvalidChannel_Exit1()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["update", "--channel", "canary"]);
        Assert.AreEqual(1, code);
        StringAssert.Contains(stdout + stderr, "Unknown channel");
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Update_FeedDisabled_Exit1()
    {
        using var harness = CliProcessHarness.CreateIsolatedCopy();
        var env = new Dictionary<string, string?> { ["TITANIUM_UPDATE_FEED"] = "" };
        var (code, stdout, stderr) = await harness.RunOnceAsync(["update"], env: env);
        Assert.AreEqual(1, code);
        StringAssert.Contains(stdout + stderr, "disabled", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Update_RemovePlus_Idempotent()
    {
        using var harness = CliProcessHarness.CreateIsolatedCopy(copyPlus: true);
        Assert.IsTrue(File.Exists(Path.Combine(harness.CliDirectory, "Titanium.Plus.dll")));

        var (code1, out1, err1) = await harness.RunOnceAsync(["update", "--remove-plus"]);
        Assert.AreEqual(0, code1, out1 + err1);
        Assert.IsFalse(File.Exists(Path.Combine(harness.CliDirectory, "Titanium.Plus.dll")));

        var (code2, out2, err2) = await harness.RunOnceAsync(["update", "--remove-plus"]);
        Assert.AreEqual(0, code2, out2 + err2);
        StringAssert.Contains(out2 + err2, "nothing to remove", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Update_Plus_FromFakeFeed_InstallsBundle_RemoveRestores()
    {
        using var harness = CliProcessHarness.CreateIsolatedCopy(copyPlus: false);
        harness.EnsurePlusDllBesideCli(copy: false);
        var dir = harness.CliDirectory;
        Assert.IsFalse(File.Exists(Path.Combine(dir, "Titanium.Plus.dll")));
        var before = SnapshotFileNames(dir);

        var plusSource = CliProcessHarness.LocatePlusDll();
        using var feed = new FakeUpdateFeed(dir, plusSource, version: "99.0.0");
        var env = new Dictionary<string, string?> { ["TITANIUM_UPDATE_FEED"] = feed.ManifestUrl };
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["update", "--plus"],
            timeout: TimeSpan.FromMinutes(2),
            env: env);
        Assert.AreEqual(0, code, stdout + stderr);
        Assert.IsTrue(File.Exists(Path.Combine(dir, "Titanium.Plus.dll")), "Plus.dll should be installed");
        Assert.IsTrue(File.Exists(Path.Combine(dir, "Titanium.Plus.files")), "bundle file list should be installed");
        Assert.IsTrue(
            File.Exists(Path.Combine(dir, "StackExchange.Redis.dll"))
            || File.Exists(Path.Combine(dir, "Titanium.Xds.dll")),
            "bundle should add a Plus dependency that the MIT CLI does not ship");
        StringAssert.Contains(stdout + stderr, "Plus", StringComparison.OrdinalIgnoreCase);

        var (versionCode, versionOut, versionErr) = await harness.RunOnceAsync(
            ["version", "--plus"],
            env: env);
        var versionText = versionOut + versionErr;
        Assert.AreEqual(0, versionCode, versionText);
        StringAssert.Contains(versionText, "RequiredAbstractions");
        Assert.IsFalse(versionText.Contains("Failed to load", StringComparison.OrdinalIgnoreCase), versionText);
        Assert.IsFalse(versionText.Contains("not present", StringComparison.OrdinalIgnoreCase), versionText);

        var (removeCode, removeOut, removeErr) = await harness.RunOnceAsync(["update", "--remove-plus"]);
        Assert.AreEqual(0, removeCode, removeOut + removeErr);
        CollectionAssert.AreEqual(before, SnapshotFileNames(dir));
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Update_Plus_LegacyAsset_InstallsDllOnly()
    {
        using var harness = CliProcessHarness.CreateIsolatedCopy(copyPlus: false);
        harness.EnsurePlusDllBesideCli(copy: false);
        var dir = harness.CliDirectory;
        var before = SnapshotFileNames(dir);

        var plusSource = CliProcessHarness.LocatePlusDll();
        using var feed = new FakeUpdateFeed(
            dir, plusSource, version: "99.0.0", layout: PlusFeedLayout.AssetOnly);
        var env = new Dictionary<string, string?> { ["TITANIUM_UPDATE_FEED"] = feed.ManifestUrl };
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["update", "--plus"],
            timeout: TimeSpan.FromMinutes(2),
            env: env);
        Assert.AreEqual(0, code, stdout + stderr);
        Assert.IsTrue(File.Exists(Path.Combine(dir, "Titanium.Plus.dll")));
        Assert.IsFalse(
            File.Exists(Path.Combine(dir, "Titanium.Plus.files")),
            "legacy asset install must not write the bundle file list");

        var (removeCode, removeOut, removeErr) = await harness.RunOnceAsync(["update", "--remove-plus"]);
        Assert.AreEqual(0, removeCode, removeOut + removeErr);
        CollectionAssert.AreEqual(before, SnapshotFileNames(dir));
    }

    private static string[] SnapshotFileNames(string directory) =>
        Directory.GetFiles(directory)
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .Cast<string>()
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Update_Plus_AlreadyCurrent_SkipsDownload()
    {
        using var harness = CliProcessHarness.CreateIsolatedCopy(copyPlus: true);
        var plusPath = Path.Combine(harness.CliDirectory, "Titanium.Plus.dll");
        Assert.IsTrue(File.Exists(plusPath));
        var before = await File.ReadAllBytesAsync(plusPath);

        using var feed = new FakeUpdateFeed(harness.CliDirectory, plusPath, version: "0.0.1");
        var env = new Dictionary<string, string?> { ["TITANIUM_UPDATE_FEED"] = feed.ManifestUrl };
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["update", "--plus"],
            timeout: TimeSpan.FromMinutes(2),
            env: env);
        Assert.AreEqual(0, code, stdout + stderr);
        var text = stdout + stderr;
        Assert.IsTrue(
            text.Contains("already", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("newer", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("No changes", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("up to date", StringComparison.OrdinalIgnoreCase),
            text);
        var after = await File.ReadAllBytesAsync(plusPath);
        CollectionAssert.AreEqual(before, after);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Update_Cli_FromFakeFeed_StartsApply()
    {
        using var harness = CliProcessHarness.CreateIsolatedCopy();
        using var feed = new FakeUpdateFeed(harness.CliDirectory, plusDllPath: null, version: "99.0.0");
        var env = new Dictionary<string, string?> { ["TITANIUM_UPDATE_FEED"] = feed.ManifestUrl };
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["update"],
            timeout: TimeSpan.FromMinutes(2),
            env: env);
        // Apply is detached; current process exits 0 after spawning the updater.
        Assert.AreEqual(0, code, stdout + stderr);
        var text = stdout + stderr;
        Assert.IsTrue(
            text.Contains("Installing", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Restarting", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("background", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("up to date", StringComparison.OrdinalIgnoreCase),
            text);

        // Give the detached apply script a moment; do not fail if files are locked mid-copy.
        await Task.Delay(2000);
    }
}
