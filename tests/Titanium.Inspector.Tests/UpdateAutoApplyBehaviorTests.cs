using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
[DoNotParallelize]
public class UpdateAutoApplyBehaviorTests
{
    private const string AutoApplyEnv = "TITANIUM_UPDATE_AUTO_APPLY";
    private const string FeedEnv = "TITANIUM_UPDATE_FEED";

    private string? _previousAutoApply;
    private string? _previousFeed;

    [TestInitialize]
    public void Init()
    {
        _previousAutoApply = Environment.GetEnvironmentVariable(AutoApplyEnv);
        _previousFeed = Environment.GetEnvironmentVariable(FeedEnv);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(AutoApplyEnv, _previousAutoApply);
        Environment.SetEnvironmentVariable(FeedEnv, _previousFeed);
    }

    [TestMethod]
    public void ShouldAutoApplyUpdate_LoopbackHttpFeedAndAutoApply_ReturnsTrue()
    {
        Environment.SetEnvironmentVariable(AutoApplyEnv, "1");
        Environment.SetEnvironmentVariable(FeedEnv, "http://127.0.0.1:18723/release-manifest.json");

        Assert.IsTrue(UpdateService.ShouldAutoApplyUpdate());
    }

    [TestMethod]
    public void ShouldAutoApplyUpdate_AutoApplyTrueCaseInsensitive_ReturnsTrue()
    {
        Environment.SetEnvironmentVariable(AutoApplyEnv, "TRUE");
        Environment.SetEnvironmentVariable(FeedEnv, "http://localhost:8080/feed.json");

        Assert.IsTrue(UpdateService.ShouldAutoApplyUpdate());
    }

    [TestMethod]
    public void ShouldAutoApplyUpdate_AutoApplyWithoutLoopbackFeed_ReturnsFalse()
    {
        Environment.SetEnvironmentVariable(AutoApplyEnv, "1");
        Environment.SetEnvironmentVariable(FeedEnv, "https://update.example.com/manifest.json");

        Assert.IsFalse(UpdateService.ShouldAutoApplyUpdate());
    }

    [TestMethod]
    public void ShouldAutoApplyUpdate_LoopbackHttpsFeed_ReturnsFalse()
    {
        Environment.SetEnvironmentVariable(AutoApplyEnv, "1");
        Environment.SetEnvironmentVariable(FeedEnv, "https://127.0.0.1/manifest.json");

        Assert.IsFalse(UpdateService.ShouldAutoApplyUpdate());
    }

    [TestMethod]
    public void ShouldAutoApplyUpdate_MissingAutoApply_ReturnsFalse()
    {
        Environment.SetEnvironmentVariable(AutoApplyEnv, null);
        Environment.SetEnvironmentVariable(FeedEnv, "http://127.0.0.1/manifest.json");

        Assert.IsFalse(UpdateService.ShouldAutoApplyUpdate());
    }

    [TestMethod]
    public void IsLoopbackHttpUpdateFeed_AcceptsIpv6Loopback()
    {
        Assert.IsTrue(UpdateService.IsLoopbackHttpUpdateFeed("http://[::1]:9/feed.json"));
    }
}