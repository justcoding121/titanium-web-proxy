using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>The <c>waf</c> policy is read by the Plus WAF, so a typo has to fail when the configuration loads.</summary>
[TestClass]
public class WafPolicyValidationTests
{
    private static TwpConfig Config(RoutePolicyConfig policy, bool plus = true, string? wafEnabled = "true") => new()
    {
        Plus = plus
            ? new PlusConfig
            {
                Enabled = true,
                Options = wafEnabled is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["waf.enabled"] = wafEnabled },
            }
            : null,
        Clusters =
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9 }],
            },
        ],
        Routes =
        [
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Policies = [policy.Name],
            },
        ],
        Policies = [policy],
    };

    private static string Errors(RoutePolicyConfig policy, bool plus = true, string? wafEnabled = "true") =>
        string.Join("\n", TwpConfigValidator.Validate(Config(policy, plus, wafEnabled)));

    private static RoutePolicyConfig Waf(
        string? mode = null, int? paranoia = null, int? threshold = null, string[]? disabled = null, string[]? exclusions = null) => new()
    {
        Name = "strict",
        Kind = "waf",
        Mode = mode,
        ParanoiaLevel = paranoia,
        BlockThreshold = threshold,
        DisabledRules = disabled,
        Exclusions = exclusions,
    };

    [TestMethod]
    public void AWellFormedPolicy_IsAccepted()
    {
        Assert.AreEqual(string.Empty, Errors(Waf()));
        Assert.AreEqual(
            string.Empty,
            Errors(Waf("block", 2, 8, ["TW-SQLI-008"], ["sqli@query:q", "TW-XSS-001", "*@header:Referer", "xss@cookie", "lfi@path", "*@body:comment"])));
        Assert.AreEqual(string.Empty, Errors(Waf("OFF")));
    }

    [TestMethod]
    public void Mode_Paranoia_AndThreshold_AreRangeChecked()
    {
        StringAssert.Contains(Errors(Waf(mode: "audit")), "mode must be off, detect, or block");
        StringAssert.Contains(Errors(Waf(paranoia: 0)), "paranoiaLevel must be 1-3");
        StringAssert.Contains(Errors(Waf(paranoia: 4)), "paranoiaLevel must be 1-3");
        StringAssert.Contains(Errors(Waf(threshold: 0)), "blockThreshold must be 1-1000");
        StringAssert.Contains(Errors(Waf(threshold: 1001)), "blockThreshold must be 1-1000");
    }

    [TestMethod]
    public void DisabledRules_AndExclusions_AreChecked()
    {
        StringAssert.Contains(Errors(Waf(disabled: ["TW-XSS-001", " "])), "disabledRules has an empty entry");
        foreach (var bad in new[] { "sqli@nowhere", "@query", "sqli@", "sqli@query:", " " })
        {
            StringAssert.Contains(Errors(Waf(exclusions: [bad])), "exclusion", bad);
        }
    }

    [TestMethod]
    public void TheWaf_NeedsPlusWithTheWafEnabled()
    {
        StringAssert.Contains(Errors(Waf(), plus: false), "needs plus.enabled: true and plus.options waf.enabled");
        StringAssert.Contains(Errors(Waf(), wafEnabled: null), "waf.enabled");
        StringAssert.Contains(Errors(Waf(), wafEnabled: "false"), "waf.enabled");
        Assert.AreEqual(string.Empty, Errors(Waf(), wafEnabled: "1"));
    }

    [TestMethod]
    public void WafSchema_ListsTheWafPolicyFields()
    {
        var schema = TwpConfigSchema.Generate();
        foreach (var field in new[] { "paranoiaLevel", "blockThreshold", "disabledRules", "exclusions" })
        {
            StringAssert.Contains(schema, $"\"{field}\"");
        }
    }
}
