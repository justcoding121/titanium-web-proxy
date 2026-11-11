using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus;

namespace Titanium.Plus.Tests;

/// <summary>
///     Pure <see cref="TitaniumPlusModule.ParseDashboardOidc"/> coverage.
///     Bug classes: partial OIDC config loads, allowlists are ignored, legacy keys are dropped.
/// </summary>
[TestClass]
public class DashboardOidcParsingBehaviorTests
{
    private static readonly string[] ExpectedOperatorGroups = ["ops", "admin", "lead"];
    private static readonly string[] ExpectedViewerGroups = ["viewers"];
    private static readonly string[] ExpectedAllowedEmails = ["a@t.test", "b@t.test"];
    private static readonly string[] ExpectedAllowedEmailDomains = ["corp.test", "partner.test"];

    [TestMethod]
    public void ParseDashboardOidc_EmptyOptions_ReturnsNull()
    {
        Assert.IsNull(TitaniumPlusModule.ParseDashboardOidc(new Dictionary<string, string>()));
        Assert.IsNull(TitaniumPlusModule.ParseDashboardOidc(new Dictionary<string, string>
        {
            ["controlPlane.dashboardOidc.issuer"] = "   ",
        }));
    }

    [TestMethod]
    public void ParseDashboardOidc_IssuerWithoutClientId_Throws()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => TitaniumPlusModule.ParseDashboardOidc(
            new Dictionary<string, string>
            {
                ["controlPlane.dashboardOidc.issuer"] = "https://idp.test/",
            }));
        StringAssert.Contains(ex.Message, "issuer and clientId");
    }

    [TestMethod]
    public void ParseDashboardOidc_MissingCookieSecretOrRedirect_Throws()
    {
        var baseOpts = new Dictionary<string, string>
        {
            ["controlPlane.dashboardOidc.issuer"] = "https://idp.test/",
            ["controlPlane.dashboardOidc.clientId"] = "dash",
            ["controlPlane.dashboardOidc.operatorGroups"] = "ops",
        };

        var missingSecret = Assert.ThrowsExactly<InvalidOperationException>(() =>
            TitaniumPlusModule.ParseDashboardOidc(baseOpts));
        StringAssert.Contains(missingSecret.Message, "cookieSecret");

        baseOpts["controlPlane.dashboardOidc.cookieSecret"] = "cookie-secret";
        var missingRedirect = Assert.ThrowsExactly<InvalidOperationException>(() =>
            TitaniumPlusModule.ParseDashboardOidc(baseOpts));
        StringAssert.Contains(missingRedirect.Message, "redirectUri");
    }

    [TestMethod]
    public void ParseDashboardOidc_RequiresAtLeastOneAllowlistDimension()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => TitaniumPlusModule.ParseDashboardOidc(
            new Dictionary<string, string>
            {
                ["controlPlane.dashboardOidc.issuer"] = "https://idp.test/",
                ["controlPlane.dashboardOidc.clientId"] = "dash",
                ["controlPlane.dashboardOidc.cookieSecret"] = "cookie-secret",
                ["controlPlane.dashboardOidc.redirectUri"] = "https://dash.test/cb",
            }));
        StringAssert.Contains(ex.Message, "allowedEmails");
    }

    [TestMethod]
    public void ParseDashboardOidc_LegacyKeys_AndSplitList_AreAccepted()
    {
        // Bug class: only modern keys work, or comma/semicolon lists collapse to one group.
        var options = TitaniumPlusModule.ParseDashboardOidc(new Dictionary<string, string>
        {
            ["dashboard.oidc.issuer"] = " https://idp.legacy/ ",
            ["dashboard.oidc.clientId"] = " legacy-client ",
            ["dashboard.oidc.clientSecret"] = "cs",
            ["dashboard.oidc.cookieSecret"] = "cookie",
            ["dashboard.oidc.redirectUri"] = "https://dash.test/cb",
            ["dashboard.oidc.groupsClaim"] = "groups.claim",
            ["dashboard.oidc.operatorGroups"] = "ops, admin; lead",
            ["dashboard.oidc.viewerGroups"] = "viewers",
            ["dashboard.oidc.allowedEmails"] = "a@t.test; b@t.test",
            ["dashboard.oidc.allowedEmailDomains"] = "corp.test, , ; partner.test",
        });

        Assert.IsNotNull(options);
        Assert.AreEqual("https://idp.legacy/", options!.Issuer);
        Assert.AreEqual("legacy-client", options.ClientId);
        Assert.AreEqual("cs", options.ClientSecret);
        Assert.AreEqual("groups.claim", options.GroupsClaim);
        CollectionAssert.AreEqual(ExpectedOperatorGroups, options.OperatorGroups!.ToArray());
        CollectionAssert.AreEqual(ExpectedViewerGroups, options.ViewerGroups!.ToArray());
        CollectionAssert.AreEqual(ExpectedAllowedEmails, options.AllowedEmails!.ToArray());
        CollectionAssert.AreEqual(ExpectedAllowedEmailDomains, options.AllowedEmailDomains!.ToArray());
    }

    [TestMethod]
    public void ParseDashboardOidc_WhitespaceOnlyListEntries_DoNotCountAsAllowlist()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => TitaniumPlusModule.ParseDashboardOidc(
            new Dictionary<string, string>
            {
                ["controlPlane.dashboardOidc.issuer"] = "https://idp.test/",
                ["controlPlane.dashboardOidc.clientId"] = "dash",
                ["controlPlane.dashboardOidc.cookieSecret"] = "cookie",
                ["controlPlane.dashboardOidc.redirectUri"] = "https://dash.test/cb",
                ["controlPlane.dashboardOidc.operatorGroups"] = " , ; ",
            }));
        StringAssert.Contains(ex.Message, "allowedEmails");
    }
}
