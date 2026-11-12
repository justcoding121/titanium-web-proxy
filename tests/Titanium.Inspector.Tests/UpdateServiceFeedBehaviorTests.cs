using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Update check and download behavior against a scripted HTTP handler (no network, no Avalonia): the operator must
///     be offered the right release for the selected channel, and a bad package must never reach the apply helper.
/// </summary>
[TestClass]
[DoNotParallelize]
public class UpdateServiceFeedBehaviorTests
{
    private const string FeedEnv = "TITANIUM_UPDATE_FEED";
    private const string FeedUrl = "http://feed.test/manifest.json";
    private const string LatestUrl = "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases/latest";
    private const string ReleasesUrl = "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases";

    private string? _previousFeed;
    private string _settingsPath = null!;

    [TestInitialize]
    public void Init()
    {
        _previousFeed = Environment.GetEnvironmentVariable(FeedEnv);
        _settingsPath = Path.Combine(Path.GetTempPath(), "twp-upd-" + Guid.NewGuid().ToString("N") + ".json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(FeedEnv, _previousFeed);
        try
        {
            File.Delete(_settingsPath);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    // Bug class: an empty feed variable (opt-out) still reaches the internet or reports a misleading error.
    [TestMethod]
    public async Task Check_WithFeedDisabled_ReportsNoManifest_AndRecordsTheAttempt()
    {
        Environment.SetEnvironmentVariable(FeedEnv, string.Empty);
        var settings = new SettingsService(_settingsPath);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var result = await Service(settings, _ => throw new InvalidOperationException("must not hit the network"))
            .CheckAsync();

        Assert.IsFalse(result.UpdateAvailable);
        Assert.AreEqual("Update check failed: no release manifest.", result.Message);
        Assert.IsTrue(settings.Current.LastUpdateCheckUtc >= before, "the check time must be persisted");
    }

    // Bug class: the Beta channel with no prerelease says "manifest failed" instead of "no beta".
    [TestMethod]
    public async Task Check_BetaChannel_WithFeedDisabled_SaysNoBetaRelease()
    {
        Environment.SetEnvironmentVariable(FeedEnv, string.Empty);
        var settings = new SettingsService(_settingsPath);
        settings.Current.UpdateChannel = "Beta";

        var result = await Service(settings, _ => throw new InvalidOperationException()).CheckAsync();

        Assert.AreEqual("No beta release found.", result.Message);
        Assert.AreEqual("Beta", result.ChannelDisplay);
    }

    // Bug class: a newer feed release is not offered, or is offered without the package for this platform.
    [TestMethod]
    public async Task Check_FeedAhead_OffersUpgradeWithThisPlatformsAsset()
    {
        Environment.SetEnvironmentVariable(FeedEnv, FeedUrl);
        var rid = UpdateService.SuggestRid();
        var manifest = "{\"version\":\"99.0.0\",\"products\":{\"inspector\":{\"assets\":{\"" + rid
            + "\":{\"url\":\"http://dl.test/inspector.zip\",\"sha256\":\"abc123\"},"
            + "\"other-rid\":{\"url\":\"http://dl.test/other.zip\"}}}}}";

        var result = await Service(
            new SettingsService(_settingsPath),
            request => request.RequestUri!.ToString() == FeedUrl ? Json(manifest) : NotFound()).CheckAsync();

        Assert.IsTrue(result.UpdateAvailable, result.Message);
        Assert.AreEqual("99.0.0", result.RemoteVersion);
        Assert.AreEqual("http://dl.test/inspector.zip", result.AssetUrl);
        Assert.AreEqual("abc123", result.AssetSha256);
        Assert.AreEqual(UpdateApplyKind.Zip, result.ApplyKind);
        Assert.AreEqual(UpdateOfferKind.Upgrade, result.OfferKind);
        Assert.IsFalse(result.IsDowngrade);
        Assert.AreEqual("Update available: 99.0.0 (Stable)", result.Message);
    }

    // Bug class: a feed ahead of us without a package for this platform offers a download of "null".
    [TestMethod]
    public async Task Check_FeedAheadWithoutAPackage_StillReportsAvailabilityButHasNoAsset()
    {
        Environment.SetEnvironmentVariable(FeedEnv, FeedUrl);

        var result = await Service(
            new SettingsService(_settingsPath),
            _ => Json("""{"version":"99.0.0","products":{"inspector":{"assets":{"other-rid":{"url":"http://x/y.zip"}}}}}"""))
            .CheckAsync();

        Assert.IsTrue(result.UpdateAvailable);
        Assert.IsNull(result.AssetUrl);
        StringAssert.Contains(result.Message, "no package was found");
        StringAssert.Contains(result.Message, "99.0.0 (Stable)");
    }

    // Bug class: a feed older than the running build is offered as an "update" (silent downgrade).
    [TestMethod]
    public async Task Check_FeedOlderThanLocal_IsUpToDate_WithoutAnAsset()
    {
        Environment.SetEnvironmentVariable(FeedEnv, FeedUrl);

        var result = await Service(new SettingsService(_settingsPath), _ => Json("""{"version":"0.0.1"}""")).CheckAsync();

        Assert.IsFalse(result.UpdateAvailable);
        Assert.IsNull(result.AssetUrl);
        Assert.AreEqual("0.0.1", result.RemoteVersion);
        Assert.AreEqual("Titanium Inspector is up to date (Stable).", result.Message);
    }

    // Bug class: a network failure escapes CheckAsync as an exception and crashes the startup check.
    [TestMethod]
    public async Task Check_WhenTheFeedRequestFails_ReturnsAFailureMessageInsteadOfThrowing()
    {
        Environment.SetEnvironmentVariable(FeedEnv, FeedUrl);

        var result = await Service(
            new SettingsService(_settingsPath),
            _ => throw new HttpRequestException("boom")).CheckAsync();

        Assert.IsFalse(result.UpdateAvailable);
        Assert.AreEqual("Update check failed: boom", result.Message);
    }

    // Bug class: a feed that is not JSON crashes instead of producing a failure message.
    [TestMethod]
    public async Task Check_WhenTheFeedIsNotJson_ReturnsAFailureMessage()
    {
        Environment.SetEnvironmentVariable(FeedEnv, FeedUrl);

        var result = await Service(new SettingsService(_settingsPath), _ => Json("<html>not json</html>")).CheckAsync();

        Assert.IsFalse(result.UpdateAvailable);
        StringAssert.StartsWith(result.Message, "Update check failed:");
    }

    // Bug class: without a custom feed the stable channel reads the wrong GitHub endpoint or ignores the
    // release-manifest asset (so the package list is lost).
    [TestMethod]
    public async Task Check_StableViaGitHub_FollowsTheReleaseManifestAsset()
    {
        Environment.SetEnvironmentVariable(FeedEnv, null);
        var rid = UpdateService.SuggestRid();
        var requested = new List<string>();

        var result = await Service(
            new SettingsService(_settingsPath),
            request =>
            {
                var url = request.RequestUri!.ToString();
                requested.Add(url);
                return url switch
                {
                    LatestUrl => Json("""
                        {"tag_name":"v99.1.0","assets":[
                          {"name":"notes.txt","browser_download_url":"http://dl.test/notes.txt"},
                          {"name":"Release-Manifest.json","browser_download_url":"http://dl.test/m.json"}]}
                        """),
                    "http://dl.test/m.json" => Json(
                        "{\"products\":{\"inspector\":{\"assets\":{\"" + rid + "\":{\"url\":\"http://dl.test/i.zip\"}}}}}"),
                    _ => NotFound(),
                };
            }).CheckAsync();

        Assert.IsTrue(result.UpdateAvailable, result.Message);
        Assert.AreEqual("99.1.0", result.RemoteVersion, "tag v-prefix must be stripped and fill the missing version");
        Assert.AreEqual("http://dl.test/i.zip", result.AssetUrl);
        CollectionAssert.AreEqual(new[] { LatestUrl, "http://dl.test/m.json" }, requested);
    }

    // Bug class: a release with no manifest asset (or no assets at all) is dropped instead of offered without a package.
    [TestMethod]
    [DataRow("""{"tag_name":"v99.1.0","assets":[]}""")]
    [DataRow("""{"tag_name":"v99.1.0"}""")]
    [DataRow("""{"tag_name":"v99.1.0","assets":[{"name":"release-manifest.json","browser_download_url":null}]}""")]
    public async Task Check_StableViaGitHub_WithoutAUsableManifestAsset_OffersTheTagWithoutAPackage(string release)
    {
        Environment.SetEnvironmentVariable(FeedEnv, null);

        var result = await Service(
            new SettingsService(_settingsPath),
            request => request.RequestUri!.ToString() == LatestUrl ? Json(release) : NotFound()).CheckAsync();

        Assert.IsTrue(result.UpdateAvailable, result.Message);
        Assert.AreEqual("99.1.0", result.RemoteVersion);
        Assert.IsNull(result.AssetUrl);
    }

    // Bug class: a manifest asset that deserializes to null aborts the loop and loses the release tag.
    [TestMethod]
    public async Task Check_StableViaGitHub_WhenManifestAssetIsJsonNull_FallsBackToTheTag()
    {
        Environment.SetEnvironmentVariable(FeedEnv, null);

        var result = await Service(
            new SettingsService(_settingsPath),
            request => request.RequestUri!.ToString() switch
            {
                LatestUrl => Json("""{"tag_name":"v99.1.0","assets":[{"name":"release-manifest.json","browser_download_url":"http://dl.test/m.json"}]}"""),
                "http://dl.test/m.json" => Json("null"),
                _ => NotFound(),
            }).CheckAsync();

        Assert.IsTrue(result.UpdateAvailable, result.Message);
        Assert.AreEqual("99.1.0", result.RemoteVersion);
    }

    // Bug class: the Beta channel picks the newest stable release or the wrong list entry.
    [TestMethod]
    public async Task Check_BetaViaGitHub_PicksTheFirstPrerelease()
    {
        Environment.SetEnvironmentVariable(FeedEnv, null);
        var settings = new SettingsService(_settingsPath);
        settings.Current.UpdateChannel = "Beta";

        var result = await Service(
            settings,
            request => request.RequestUri!.ToString() == ReleasesUrl
                ? Json("""
                    [{"prerelease":false,"tag_name":"v98.0.0"},
                     {"prerelease":true,"tag_name":"v99.0.0-beta.2"},
                     {"prerelease":true,"tag_name":"v99.0.0-beta.1"}]
                    """)
                : NotFound()).CheckAsync();

        Assert.IsTrue(result.UpdateAvailable, result.Message);
        Assert.AreEqual("99.0.0-beta.2", result.RemoteVersion);
        Assert.AreEqual("Beta", result.ChannelDisplay);
        // The listed release carries no manifest asset, so it is offered by tag with no package.
        StringAssert.Contains(result.Message, "Install 99.0.0-beta.2 (Beta) is available");
        Assert.IsNull(result.AssetUrl);
    }

    // Bug class: with no prerelease published the Beta channel offers a stable build or throws.
    [TestMethod]
    public async Task Check_BetaViaGitHub_WithoutPrereleases_SaysNoBetaFound()
    {
        Environment.SetEnvironmentVariable(FeedEnv, null);
        var settings = new SettingsService(_settingsPath);
        settings.Current.UpdateChannel = "Beta";

        var result = await Service(
            settings,
            request => request.RequestUri!.ToString() == ReleasesUrl
                ? Json("""[{"prerelease":false,"tag_name":"v98.0.0"}]""")
                : NotFound()).CheckAsync();

        Assert.IsFalse(result.UpdateAvailable);
        Assert.AreEqual("No beta release found.", result.Message);
    }

    // Bug class: a GitHub 404/500 is reported as success or crashes the caller.
    [TestMethod]
    public async Task Check_StableViaGitHub_WhenTheApiReturnsAnError_ReturnsAFailureMessage()
    {
        Environment.SetEnvironmentVariable(FeedEnv, null);

        var result = await Service(new SettingsService(_settingsPath), _ => NotFound()).CheckAsync();

        Assert.IsFalse(result.UpdateAvailable);
        StringAssert.StartsWith(result.Message, "Update check failed:");
    }

    // Bug class: download is attempted with no URL / for a check that found nothing.
    [TestMethod]
    [DataRow(false, "http://dl.test/i.zip")]
    [DataRow(true, null)]
    [DataRow(true, "")]
    public async Task Download_WithoutAnOfferedPackage_RefusesWithoutTouchingTheNetwork(bool available, string? url)
    {
        var result = await Service(
                new SettingsService(_settingsPath),
                _ => throw new InvalidOperationException("must not download"))
            .DownloadAndStartApplyAsync(new UpdateCheckResult { UpdateAvailable = available, AssetUrl = url });

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("No update package to install.", result.Message);
    }

    // Bug class: a tampered package is written to disk and handed to the apply helper.
    [TestMethod]
    public async Task Download_WhenChecksumDiffers_AbortsAndWritesNothing()
    {
        var version = "99.9.9-test" + Guid.NewGuid().ToString("N")[..8];
        var settings = new SettingsService(_settingsPath);

        var result = await Service(
                settings,
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) })
            .DownloadAndStartApplyAsync(new UpdateCheckResult
            {
                UpdateAvailable = true,
                AssetUrl = "http://dl.test/i.zip",
                AssetSha256 = new string('0', 64),
                RemoteVersion = version,
                ChannelDisplay = "Stable",
            });

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("SHA256 mismatch — aborting update.", result.Message);
        Assert.IsFalse(
            File.Exists(Path.Combine(Path.GetTempPath(), "TitaniumInspector-update", $"TitaniumInspector-{version}.zip")),
            "a rejected package must not be left for the apply helper");
        Assert.IsNull(settings.Current.InstalledReleaseTag, "a failed update must not claim the build is installed");
    }

    // Bug class: a download failure surfaces as an unhandled exception instead of a status message.
    [TestMethod]
    public async Task Download_WhenTheRequestFails_ReturnsTheError()
    {
        var settings = new SettingsService(_settingsPath);

        var result = await Service(settings, _ => throw new HttpRequestException("connection reset"))
            .DownloadAndStartApplyAsync(new UpdateCheckResult
            {
                UpdateAvailable = true,
                AssetUrl = "http://dl.test/i.zip",
                RemoteVersion = "99.9.9",
                ChannelDisplay = "Stable",
            });

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("Update failed: connection reset", result.Message);
        Assert.IsNull(settings.Current.InstalledReleaseTag);
    }

    private static UpdateService Service(SettingsService settings, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(settings, () => new HttpClient(new ScriptedHandler(respond)));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
