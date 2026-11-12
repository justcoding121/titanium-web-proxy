using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Ingress annotation parse edges that docs tests skip.
///     Bug classes: credentialed auth-url accepted, canary-weight without canary, invalid weight/RPS slip through.
/// </summary>
[TestClass]
public sealed class IngressAnnotationsEdgeBehaviorTests
{
    [TestMethod]
    public void Parse_AuthUrl_RejectsCredentialsAndBadSchemes()
    {
        var bad = IngressAnnotations.Parse(new Dictionary<string, string>
        {
            [IngressAnnotations.Prefix + "auth-url"] = "https://user:pass@idp.example/oauth",
        }, "default");
        Assert.IsTrue(bad.Problems.Count > 0);

        var ftp = IngressAnnotations.Parse(new Dictionary<string, string>
        {
            [IngressAnnotations.Prefix + "auth-url"] = "ftp://idp.example/oauth",
        }, "default");
        Assert.IsTrue(ftp.Problems.Count > 0);

        var ok = IngressAnnotations.Parse(new Dictionary<string, string>
        {
            [IngressAnnotations.Prefix + "auth-url"] = "https://idp.example/oauth",
        }, "default");
        Assert.AreEqual(0, ok.Problems.Count);
        Assert.AreEqual("https://idp.example/oauth", ok.AuthUrl);
    }

    [TestMethod]
    public void Parse_CanaryWeight_RequiresCanaryTrue_AndValidPercent()
    {
        var orphan = IngressAnnotations.Parse(new Dictionary<string, string>
        {
            [IngressAnnotations.Prefix + "canary-weight"] = "25",
        }, "default");
        Assert.IsTrue(orphan.Problems.Exists(static p => p.Contains("needs", StringComparison.Ordinal)));

        var bad = IngressAnnotations.Parse(new Dictionary<string, string>
        {
            [IngressAnnotations.Prefix + "canary"] = "true",
            [IngressAnnotations.Prefix + "canary-weight"] = "101",
        }, "default");
        Assert.IsTrue(bad.Problems.Count > 0);

        var ok = IngressAnnotations.Parse(new Dictionary<string, string>
        {
            [IngressAnnotations.Prefix + "canary"] = "true",
            [IngressAnnotations.Prefix + "canary-weight"] = "25",
        }, "default");
        Assert.AreEqual(0, ok.Problems.Count);
        Assert.IsTrue(ok.Canary);
        Assert.AreEqual(25, ok.CanaryWeight);
    }

    [TestMethod]
    public void Parse_ProxyBodySize_AndRateLimitRps()
    {
        var zero = IngressAnnotations.Parse(new Dictionary<string, string>
        {
            [IngressAnnotations.Prefix + "proxy-body-size"] = "0",
        }, "default");
        Assert.AreEqual(0, zero.Problems.Count);
        Assert.AreEqual(0L, zero.BodyLimitBytes);

        var rpsBad = IngressAnnotations.Parse(new Dictionary<string, string>
        {
            [IngressAnnotations.Prefix + "limit-rps"] = "0",
        }, "default");
        Assert.IsTrue(rpsBad.Problems.Count > 0);

        var rpsOk = IngressAnnotations.Parse(new Dictionary<string, string>
        {
            [IngressAnnotations.Prefix + "limit-rps"] = "100",
        }, "default");
        Assert.AreEqual(0, rpsOk.Problems.Count);
        Assert.AreEqual(100, rpsOk.LimitRps);
    }
}
