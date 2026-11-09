using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Security.Oidc;

namespace Titanium.Plus.Tests;

/// <summary>
///     Unit tests for <see cref="DashboardOidc.Create"/> validation paths.
///     Bug class: misconfigured OIDC options accepted silently and surfaced only at runtime
///     (or, conversely, a valid config rejected with an unhelpful error).
/// </summary>
[TestClass]
public class DashboardOidcValidationTests
{
    private const string ValidCookieSecret = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // 64 hex chars → 32 bytes
    private const string ValidIssuer = "https://idp.example.test";
    private const string ValidClientId = "my-client";
    private const string ValidRedirectUri = "https://app.example.test/_titanium/oidc/callback";
    private static readonly string[] ValidAllowedEmails = ["admin@example.test"];

    private static DashboardOidcOptions ValidOptions() => new()
    {
        Issuer = ValidIssuer,
        ClientId = ValidClientId,
        CookieSecret = ValidCookieSecret,
        RedirectUri = ValidRedirectUri,
        AllowedEmails = ValidAllowedEmails,
    };

    // ---------------------------------------------------------------- Issuer / ClientId

    [TestMethod]
    public void Create_ThrowsOidcConfigException_WhenIssuerIsEmpty()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = string.Empty,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = ValidRedirectUri,
            AllowedEmails = ValidAllowedEmails,
        };
        Assert.ThrowsExactly<OidcConfigException>(() => DashboardOidc.Create(opts));
    }

    [TestMethod]
    public void Create_ThrowsOidcConfigException_WhenIssuerIsWhitespace()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = "   ",
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = ValidRedirectUri,
            AllowedEmails = ValidAllowedEmails,
        };
        Assert.ThrowsExactly<OidcConfigException>(() => DashboardOidc.Create(opts));
    }

    [TestMethod]
    public void Create_ThrowsOidcConfigException_WhenClientIdIsEmpty()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = string.Empty,
            CookieSecret = ValidCookieSecret,
            RedirectUri = ValidRedirectUri,
            AllowedEmails = ValidAllowedEmails,
        };
        Assert.ThrowsExactly<OidcConfigException>(() => DashboardOidc.Create(opts));
    }

    // ---------------------------------------------------------------- RedirectUri

    [TestMethod]
    public void Create_ThrowsOidcConfigException_WhenRedirectUriIsEmpty()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = string.Empty,
            AllowedEmails = ValidAllowedEmails,
        };
        Assert.ThrowsExactly<OidcConfigException>(() => DashboardOidc.Create(opts));
    }

    [TestMethod]
    public void Create_ThrowsOidcConfigException_WhenRedirectUriIsRelative()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = "/callback",
            AllowedEmails = ValidAllowedEmails,
        };
        Assert.ThrowsExactly<OidcConfigException>(() => DashboardOidc.Create(opts));
    }

    [TestMethod]
    public void Create_ThrowsOidcConfigException_WhenRedirectUriSchemeIsNotHttpOrHttps()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = "ftp://app.example.test/cb",
            AllowedEmails = ValidAllowedEmails,
        };
        Assert.ThrowsExactly<OidcConfigException>(() => DashboardOidc.Create(opts));
    }

    [TestMethod]
    public void Create_AcceptsHttpRedirectUri()
    {
        // http:// is allowed for localhost / dev setups.
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = "http://localhost:5000/cb",
            AllowedEmails = ValidAllowedEmails,
        };
        using var oidc = DashboardOidc.Create(opts);
        Assert.IsNotNull(oidc);
    }

    // ---------------------------------------------------------------- Allowed-users guards

    [TestMethod]
    public void Create_ThrowsOidcConfigException_WhenNoAllowedUsersConfigured()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = ValidRedirectUri,
            // No AllowedEmails, AllowedEmailDomains, OperatorGroups, or ViewerGroups
        };
        Assert.ThrowsExactly<OidcConfigException>(() => DashboardOidc.Create(opts));
    }

    [TestMethod]
    public void Create_Succeeds_WithOperatorGroupsOnly()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = ValidRedirectUri,
            OperatorGroups = ["ops"],
        };
        using var oidc = DashboardOidc.Create(opts);
        Assert.IsNotNull(oidc);
    }

    [TestMethod]
    public void Create_Succeeds_WithViewerGroupsOnly()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = ValidRedirectUri,
            ViewerGroups = ["viewers"],
        };
        using var oidc = DashboardOidc.Create(opts);
        Assert.IsNotNull(oidc);
    }

    [TestMethod]
    public void Create_Succeeds_WithAllowedEmailDomainsOnly()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = ValidRedirectUri,
            AllowedEmailDomains = ["example.test"],
        };
        using var oidc = DashboardOidc.Create(opts);
        Assert.IsNotNull(oidc);
    }

    // ---------------------------------------------------------------- CookieSecret

    [TestMethod]
    public void Create_ThrowsOidcConfigException_WhenCookieSecretTooShort()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = "tooshort",
            RedirectUri = ValidRedirectUri,
            AllowedEmails = ValidAllowedEmails,
        };
        // OidcCookieCrypto.ParseKeys throws OidcConfigException; Create wraps it.
        Assert.ThrowsExactly<OidcConfigException>(() => DashboardOidc.Create(opts));
    }

    [TestMethod]
    public void Create_ThrowsOidcConfigException_WhenCookieSecretIsEmpty()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = string.Empty,
            RedirectUri = ValidRedirectUri,
            AllowedEmails = ValidAllowedEmails,
        };
        Assert.ThrowsExactly<OidcConfigException>(() => DashboardOidc.Create(opts));
    }

    // ---------------------------------------------------------------- Construction counter

    [TestMethod]
    public void ResetConstructedProviders_SetsCounterToZero()
    {
        DashboardOidc.ResetConstructedProviders();
        var before = DashboardOidc.ConstructedProviders;
        Assert.AreEqual(0, before);

        using var oidc = DashboardOidc.Create(ValidOptions());
        Assert.AreEqual(1, DashboardOidc.ConstructedProviders);

        DashboardOidc.ResetConstructedProviders();
        Assert.AreEqual(0, DashboardOidc.ConstructedProviders);
    }

    // ---------------------------------------------------------------- Null guard

    [TestMethod]
    public void Create_ThrowsArgumentNullException_WhenOptionsIsNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => DashboardOidc.Create(null!));
    }

    // ---------------------------------------------------------------- Optional fields

    [TestMethod]
    public void Create_Succeeds_WithClientSecret()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            ClientSecret = "secret-value",
            CookieSecret = ValidCookieSecret,
            RedirectUri = ValidRedirectUri,
            AllowedEmails = ValidAllowedEmails,
        };
        using var oidc = DashboardOidc.Create(opts);
        Assert.IsNotNull(oidc);
    }

    [TestMethod]
    public void Create_Succeeds_WithGroupsClaim()
    {
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = ValidRedirectUri,
            AllowedEmails = ValidAllowedEmails,
            GroupsClaim = "groups",
        };
        using var oidc = DashboardOidc.Create(opts);
        Assert.IsNotNull(oidc);
    }

    [TestMethod]
    public void Create_Succeeds_WithCustomHttpHandler()
    {
        // HttpHandler != null means ownsHandler = false, so the handler is not disposed.
        using var handler = new System.Net.Http.HttpClientHandler();
        var opts = new DashboardOidcOptions
        {
            Issuer = ValidIssuer,
            ClientId = ValidClientId,
            CookieSecret = ValidCookieSecret,
            RedirectUri = ValidRedirectUri,
            AllowedEmails = ValidAllowedEmails,
            HttpHandler = handler,
        };
        using var oidc = DashboardOidc.Create(opts);
        Assert.IsNotNull(oidc);
    }
}
