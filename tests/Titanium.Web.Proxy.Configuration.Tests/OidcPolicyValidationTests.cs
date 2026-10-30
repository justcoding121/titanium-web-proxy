using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>
/// The <c>oidc</c> policy is enforced by Plus and refuses every request when it is half-written, so the
/// mistakes that would only show up as a 503 at run time have to fail when the configuration loads.
/// </summary>
[TestClass]
public class OidcPolicyValidationTests
{
    private static RoutePolicyConfig Good(Func<RoutePolicyConfig, RoutePolicyConfig>? shape = null)
    {
        var policy = new RoutePolicyConfig
        {
            Name = "login",
            Kind = "oidc",
            Issuer = "https://idp.example.test/realms/main",
            ClientId = "client",
            ClientSecretEnv = "OIDC_SECRET",
            CookieSecretEnv = "OIDC_COOKIE_KEY",
        };
        return shape is null ? policy : shape(policy);
    }

    private static RoutePolicyConfig With(
        string? issuer = "https://idp.example.test", string? clientId = "client", string? clientSecretEnv = null,
        string? clientSecretFile = null, string? cookieSecretEnv = "KEY", string? cookieSecretFile = null,
        string? redirectPath = null, string? logoutPath = null, string? cookieName = null, int? ttl = null,
        string? publicBaseUrl = null, string[]? scopes = null, string[]? emails = null, string[]? domains = null,
        string[]? groups = null, string[]? allowlist = null, string? groupsClaim = null,
        IReadOnlyDictionary<string, string>? passClaims = null) => new()
    {
        Name = "login",
        Kind = "oidc",
        Issuer = issuer,
        ClientId = clientId,
        ClientSecretEnv = clientSecretEnv,
        ClientSecretFile = clientSecretFile,
        CookieSecretEnv = cookieSecretEnv,
        CookieSecretFile = cookieSecretFile,
        RedirectPath = redirectPath,
        LogoutPath = logoutPath,
        CookieName = cookieName,
        SessionTtlSeconds = ttl,
        PublicBaseUrl = publicBaseUrl,
        Scopes = scopes,
        AllowedEmails = emails,
        AllowedEmailDomains = domains,
        AllowedGroups = groups,
        PostLoginRedirectAllowlist = allowlist,
        GroupsClaim = groupsClaim,
        PassClaims = passClaims,
    };

    private static TwpConfig Config(RoutePolicyConfig policy, bool plus = true) => new()
    {
        Plus = plus ? new PlusConfig { Enabled = true } : null,
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

    private static string Errors(RoutePolicyConfig policy, bool plus = true) =>
        string.Join("\n", TwpConfigValidator.Validate(Config(policy, plus)));

    [TestMethod]
    public void AWorkingPolicy_IsAccepted()
    {
        Assert.AreEqual(string.Empty, Errors(Good()));
    }

    [TestMethod]
    public void AFullPolicy_IsAccepted()
    {
        var policy = With(
            issuer: "https://login.example.test/", clientSecretFile: "/run/secrets/oidc", cookieSecretEnv: null,
            cookieSecretFile: "/run/secrets/cookie", redirectPath: "/auth/callback", logoutPath: "/auth/logout",
            cookieName: "session_gate", ttl: 3600, publicBaseUrl: "https://app.example.test",
            scopes: ["openid", "email", "groups"], emails: ["a@example.test"], domains: ["example.test"],
            groups: ["admins"], allowlist: ["https://app.example.test", "http://localhost:3000"], groupsClaim: "roles",
            passClaims: new Dictionary<string, string> { ["email"] = "X-User-Email", ["sub"] = "X-User" });

        Assert.AreEqual(string.Empty, Errors(policy));
    }

    [TestMethod]
    public void ALoopbackHttpIssuer_IsAccepted_ForLocalDevelopment()
    {
        Assert.AreEqual(string.Empty, Errors(Good(static p => new RoutePolicyConfig
        {
            Name = p.Name, Kind = p.Kind, Issuer = "http://127.0.0.1:5556/realm", ClientId = p.ClientId, CookieSecretEnv = p.CookieSecretEnv,
        })));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("idp.example.test")]
    [DataRow("http://idp.example.test")]
    [DataRow("ftp://idp.example.test")]
    [DataRow("https://user:pass@idp.example.test")]
    [DataRow("https://idp.example.test?x=1")]
    [DataRow("https://idp.example.test#frag")]
    public void TheIssuer_MustBeAnHttpsUrl_WithoutCredentialsQueryOrFragment(string? issuer)
    {
        StringAssert.Contains(Errors(With(issuer: issuer)), "'login' (oidc) needs issuer");
    }

    [TestMethod]
    public void TheClientAndTheCookieKey_AreRequired()
    {
        StringAssert.Contains(Errors(With(clientId: null)), "(oidc) needs clientId");
        StringAssert.Contains(Errors(With(clientId: " ")), "(oidc) needs clientId");
        StringAssert.Contains(Errors(With(cookieSecretEnv: null)), "needs exactly one of cookieSecretEnv or cookieSecretFile");
        StringAssert.Contains(Errors(With(cookieSecretEnv: "A", cookieSecretFile: "/b")), "needs exactly one of cookieSecretEnv or cookieSecretFile");
        StringAssert.Contains(Errors(With(clientSecretEnv: "A", clientSecretFile: "/b")), "sets both clientSecretEnv and clientSecretFile");
    }

    [DataTestMethod]
    [DataRow("callback")]
    [DataRow("/")]
    [DataRow("//evil.test")]
    [DataRow("/\\evil.test")]
    [DataRow("/cb?x=1")]
    [DataRow("/cb#x")]
    [DataRow("/c b")]
    public void ThePaths_MustBeSingleSlashPaths(string path)
    {
        StringAssert.Contains(Errors(With(redirectPath: path)), "redirectPath must start with a single '/'");
        StringAssert.Contains(Errors(With(logoutPath: path)), "logoutPath must start with a single '/'");
    }

    [TestMethod]
    public void TheRedirectPathAndTheLogoutPath_MustDiffer()
    {
        StringAssert.Contains(Errors(With(redirectPath: "/auth", logoutPath: "/auth")), "redirectPath and logoutPath must differ");
    }

    [TestMethod]
    public void TheCookieNameAndLifetime_AreChecked()
    {
        StringAssert.Contains(Errors(With(cookieName: "bad name")), "cookieName must be 1-64 token characters");
        StringAssert.Contains(Errors(With(cookieName: "a;b")), "cookieName must be 1-64 token characters");
        StringAssert.Contains(Errors(With(cookieName: new string('a', 65))), "cookieName must be 1-64 token characters");
        StringAssert.Contains(Errors(With(ttl: 59)), "sessionTtlSeconds must be 60-2592000");
        StringAssert.Contains(Errors(With(ttl: 2_592_001)), "sessionTtlSeconds must be 60-2592000");
        Assert.AreEqual(string.Empty, Errors(With(ttl: 60)));
        Assert.AreEqual(string.Empty, Errors(With(ttl: 2_592_000)));
    }

    [TestMethod]
    public void ThePublicBaseUrl_AndTheAllowlist_MustBeOrigins()
    {
        StringAssert.Contains(Errors(With(publicBaseUrl: "app.example.test")), "publicBaseUrl must be an absolute http or https origin");
        StringAssert.Contains(Errors(With(publicBaseUrl: "https://app.example.test/path")), "publicBaseUrl must be");
        StringAssert.Contains(Errors(With(allowlist: ["/relative"])), "postLoginRedirectAllowlist entry '/relative' must be an absolute http or https origin");
        StringAssert.Contains(Errors(With(allowlist: ["https://user@app.example.test"])), "postLoginRedirectAllowlist entry");
        StringAssert.Contains(Errors(With(allowlist: ["javascript:alert(1)"])), "postLoginRedirectAllowlist entry");
    }

    [TestMethod]
    public void TheLists_RejectMalformedEntries()
    {
        StringAssert.Contains(Errors(With(scopes: ["open id"])), "scopes entry 'open id'");
        StringAssert.Contains(Errors(With(emails: ["not-an-address"])), "allowedEmails entry 'not-an-address' must be an e-mail address");
        StringAssert.Contains(Errors(With(domains: ["@example.test"])), "allowedEmailDomains entry '@example.test' must be a domain without @");
        StringAssert.Contains(Errors(With(groups: [" "])), "allowedGroups entry");
        StringAssert.Contains(Errors(With(groupsClaim: " ")), "groupsClaim must not be empty");
    }

    [DataTestMethod]
    [DataRow("Authorization")]
    [DataRow("Cookie")]
    [DataRow("Set-Cookie")]
    [DataRow("Host")]
    [DataRow("Content-Length")]
    [DataRow("Transfer-Encoding")]
    [DataRow("Connection")]
    [DataRow("bad name")]
    public void PassClaims_CannotWriteAHeaderThatMeansSomethingElse(string header)
    {
        var errors = Errors(With(passClaims: new Dictionary<string, string> { ["email"] = header }));

        StringAssert.Contains(errors, "passClaims header");
    }

    [TestMethod]
    public void PassClaims_NeedAClaimName()
    {
        StringAssert.Contains(Errors(With(passClaims: new Dictionary<string, string> { [" "] = "X-User" })), "passClaims has an empty claim name");
    }

    [TestMethod]
    public void WithoutPlus_TheLoginCannotBeEnforced_SoItIsRejected()
    {
        StringAssert.Contains(Errors(Good(), plus: false), "needs plus.enabled: true");
    }

    [TestMethod]
    public void OtherPolicyKinds_AreNotAskedForPlus()
    {
        var config = Config(new RoutePolicyConfig { Name = "gate", Kind = "extAuth", Url = "https://auth.test/" }, plus: false);

        Assert.AreEqual(string.Empty, string.Join("\n", TwpConfigValidator.Validate(config)));
    }
}
