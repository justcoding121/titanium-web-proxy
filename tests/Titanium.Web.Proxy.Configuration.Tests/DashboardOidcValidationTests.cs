using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>Dashboard SSO must pin redirectUri and at least one identity/group allow-list.</summary>
[TestClass]
public class DashboardOidcValidationTests
{
    private static string Errors(PlusConfig plus) =>
        string.Join("\n", TwpConfigValidator.Validate(new TwpConfig { Plus = plus }));

    private static DashboardOidcConfig BaseOidc(
        string? redirectUri = "https://proxy.example/login/oidc/callback",
        List<string>? operatorGroups = null,
        List<string>? viewerGroups = null,
        List<string>? allowedEmails = null,
        List<string>? allowedEmailDomains = null) =>
        new()
        {
            Issuer = "https://idp.example/",
            ClientId = "titanium-dashboard",
            CookieSecretEnv = "DASHBOARD_OIDC_COOKIE_KEY",
            RedirectUri = redirectUri,
            OperatorGroups = operatorGroups,
            ViewerGroups = viewerGroups,
            AllowedEmails = allowedEmails,
            AllowedEmailDomains = allowedEmailDomains,
        };

    private static PlusConfig PlusWith(DashboardOidcConfig oidc) =>
        new()
        {
            Enabled = true,
            ControlPlane = new ControlPlaneConfig { Host = "127.0.0.1", DashboardOidc = oidc },
        };

    [TestMethod]
    public void DashboardOidc_EmptyAllowlists_IsRejected()
    {
        var errors = Errors(PlusWith(BaseOidc()));
        StringAssert.Contains(errors, "allowedEmails, allowedEmailDomains, operatorGroups, or viewerGroups");
    }

    [TestMethod]
    public void DashboardOidc_MissingRedirectUri_IsRejected()
    {
        var errors = Errors(PlusWith(BaseOidc(redirectUri: null, operatorGroups: ["ops"])));
        StringAssert.Contains(errors, "needs redirectUri");
    }

    [TestMethod]
    public void DashboardOidc_RelativeRedirectUri_IsRejected()
    {
        var errors = Errors(PlusWith(BaseOidc(redirectUri: "/login/oidc/callback", operatorGroups: ["ops"])));
        StringAssert.Contains(errors, "redirectUri must be an absolute http(s) URI");
    }

    [TestMethod]
    [DataRow("operatorGroups")]
    [DataRow("viewerGroups")]
    [DataRow("allowedEmails")]
    [DataRow("allowedEmailDomains")]
    public void DashboardOidc_AnySingleAllowlist_IsAccepted(string which)
    {
        var oidc = which switch
        {
            "operatorGroups" => BaseOidc(operatorGroups: ["ops"]),
            "viewerGroups" => BaseOidc(viewerGroups: ["view"]),
            "allowedEmails" => BaseOidc(allowedEmails: ["alice@example.com"]),
            "allowedEmailDomains" => BaseOidc(allowedEmailDomains: ["example.com"]),
            _ => throw new ArgumentOutOfRangeException(nameof(which)),
        };
        Assert.AreEqual(string.Empty, Errors(PlusWith(oidc)));
    }

    [TestMethod]
    public void DashboardOidc_WhitespaceOnlyAllowlist_IsRejected()
    {
        var errors = Errors(PlusWith(BaseOidc(operatorGroups: ["  ", ""])));
        StringAssert.Contains(errors, "allowedEmails, allowedEmailDomains, operatorGroups, or viewerGroups");
    }
}
