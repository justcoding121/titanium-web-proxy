using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Oidc;
using Titanium.TestKit;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Tests;

[TestClass]
public class OidcCookieCryptoTests
{
    private static readonly string KeyA = new('a', 64);
    private static readonly string KeyB = new('b', 64);
    private static readonly string[] InvalidKeySamples =
    [
        string.Empty, "  ", "short", new string('a', 62), Convert.ToBase64String(new byte[16]), Convert.ToBase64String(new byte[33]),
    ];

    private static OidcCookieCrypto Crypto(params string[] keys) =>
        new(OidcCookieCrypto.ParseKeys(string.Join(',', keys)));

    [TestMethod]
    public void SealThenOpen_RoundTrips_AndTwoSealsOfTheSameTextDiffer()
    {
        var crypto = Crypto(KeyA);
        var plain = Encoding.UTF8.GetBytes("{\"sub\":\"u\"}");

        var one = crypto.Seal("session", "c", plain);
        var two = crypto.Seal("session", "c", plain);

        CollectionAssert.AreEqual(plain, crypto.Open("session", "c", one));
        Assert.AreNotEqual(one, two, "every seal uses a fresh nonce");
        Assert.IsFalse(one.Contains('=') || one.Contains('+') || one.Contains('/'), "the value is cookie-safe base64url");
    }

    [TestMethod]
    public void Open_RefusesAnyChangedByte_AndTruncation()
    {
        var crypto = Crypto(KeyA);
        var sealedValue = crypto.Seal("session", "c", Encoding.UTF8.GetBytes("hello world"));
        var raw = Base64UrlEncoder.DecodeBytes(sealedValue);

        for (var i = 0; i < raw.Length; i++)
        {
            var copy = (byte[])raw.Clone();
            copy[i] ^= 0x01;
            Assert.IsNull(crypto.Open("session", "c", Base64UrlEncoder.Encode(copy)), $"byte {i}");
        }

        Assert.IsNull(crypto.Open("session", "c", Base64UrlEncoder.Encode(raw[..^1])));
        Assert.IsNull(crypto.Open("session", "c", string.Empty));
        Assert.IsNull(crypto.Open("session", "c", "!!not base64!!"));
    }

    [TestMethod]
    public void Open_IsBoundToThePurposeAndTheCookieName()
    {
        var crypto = Crypto(KeyA);
        var sealedValue = crypto.Seal("session", "c", [1, 2, 3]);

        Assert.IsNotNull(crypto.Open("session", "c", sealedValue));
        Assert.IsNull(crypto.Open("flow", "c", sealedValue));
        Assert.IsNull(crypto.Open("session", "d", sealedValue));
    }

    [TestMethod]
    public void Rotation_OpensWithAnyKey_AndSealsWithTheFirst()
    {
        var oldOnly = Crypto(KeyB).Seal("session", "c", [9]);
        var rotated = Crypto(KeyA, KeyB);

        CollectionAssert.AreEqual(new byte[] { 9 }, rotated.Open("session", "c", oldOnly));
        var fresh = rotated.Seal("session", "c", [7]);
        Assert.IsNotNull(Crypto(KeyA).Open("session", "c", fresh));
        Assert.IsNull(Crypto(KeyB).Open("session", "c", fresh));
        Assert.IsNull(Crypto(new string('c', 64)).Open("session", "c", fresh));
    }

    [TestMethod]
    public void ParseKeys_AcceptsHexAndBothBase64Forms_AndRefusesTheRest()
    {
        var bytes = Enumerable.Range(200, 32).Select(static i => (byte)i).ToArray();
        var standard = Convert.ToBase64String(bytes);
        var url = standard.Replace('+', '-').Replace('/', '_').TrimEnd('=');

        CollectionAssert.AreEqual(bytes, OidcCookieCrypto.ParseKeys(Convert.ToHexString(bytes))[0]);
        CollectionAssert.AreEqual(bytes, OidcCookieCrypto.ParseKeys(standard)[0]);
        CollectionAssert.AreEqual(bytes, OidcCookieCrypto.ParseKeys(url)[0]);
        Assert.AreEqual(2, OidcCookieCrypto.ParseKeys(KeyA + "\n" + KeyB + "\n").Count);

        foreach (var bad in InvalidKeySamples)
        {
            Assert.ThrowsExactly<OidcConfigException>(() => OidcCookieCrypto.ParseKeys(bad), bad);
        }

        Assert.ThrowsExactly<OidcConfigException>(() => OidcCookieCrypto.ParseKeys(string.Join(',', Enumerable.Repeat(KeyA, 5))));
    }

    [TestMethod]
    public void ParseKeys_NeverEchoesTheSecret()
    {
        var secret = "not-a-valid-key-but-it-is-secret";

        var ex = Assert.ThrowsExactly<OidcConfigException>(() => OidcCookieCrypto.ParseKeys(secret));

        Assert.IsFalse(ex.Message.Contains(secret, StringComparison.Ordinal));
    }
}

[TestClass]
public class OidcRedirectTests
{
    private static readonly string[] Allowed = ["https://app.example.test", "http://localhost:3000"];

    [DataTestMethod]
    [DataRow("/")]
    [DataRow("/dashboard?tab=2#top")]
    [DataRow("/a/b/c")]
    [DataRow("https://app.example.test/x")]
    [DataRow("HTTPS://APP.EXAMPLE.TEST/x")]
    [DataRow("http://localhost:3000/cb")]
    public void Resolve_AcceptsLocalPathsAndAllowListedOrigins(string target) =>
        Assert.AreEqual(target, OidcRedirect.Resolve(target, Allowed));

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("//evil.test")]
    [DataRow("///evil.test")]
    [DataRow("/\\evil.test")]
    [DataRow("\\\\evil.test")]
    [DataRow("evil.test")]
    [DataRow("https://evil.test/")]
    [DataRow("https://app.example.test.evil.test/")]
    [DataRow("https://app.example.test@evil.test/")]
    [DataRow("https://user:pass@app.example.test/")]
    [DataRow("http://app.example.test/")]
    [DataRow("https://app.example.test:8443/")]
    [DataRow("http://localhost:3001/")]
    [DataRow("javascript:alert(1)")]
    [DataRow("data:text/html,x")]
    [DataRow("/path with space")]
    [DataRow("/line\nbreak")]
    [DataRow("/tab\there")]
    [DataRow("/nul\0")]
    public void Resolve_DropsEverythingElse(string? target) =>
        Assert.IsNull(OidcRedirect.Resolve(target, Allowed));

    [TestMethod]
    public void Resolve_DropsAnOverlongTarget() =>
        Assert.IsNull(OidcRedirect.Resolve("/" + new string('a', 3000), Allowed));

    [DataTestMethod]
    [DataRow("/a?b=1", "/a?b=1")]
    [DataRow("//evil.test/x", "/evil.test/x")]
    [DataRow("////evil.test/x", "/evil.test/x")]
    [DataRow("http://matrix.test/a?b=1", "/a?b=1")]
    [DataRow("https://matrix.test", "/")]
    public void SamePathAndQuery_NeverNamesAnotherHost(string target, string expected) =>
        Assert.AreEqual(expected, OidcRedirect.SamePathAndQuery(target));

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("*")]
    [DataRow("a/b")]
    [DataRow("/a\\b")]
    public void SamePathAndQuery_DropsWhatIsNotAPath(string? target) =>
        Assert.IsNull(OidcRedirect.SamePathAndQuery(target));

    [TestMethod]
    public void OriginOf_KeepsOnlySchemeHostAndNonDefaultPort()
    {
        Assert.AreEqual("https://app.example.test", OidcRedirect.OriginOf("https://app.example.test/path?q=1"));
        Assert.AreEqual("http://localhost:3000", OidcRedirect.OriginOf("http://localhost:3000/"));
        Assert.IsNull(OidcRedirect.OriginOf("ftp://example.test"));
        Assert.IsNull(OidcRedirect.OriginOf("not a url"));
    }
}

[TestClass]
public class OidcGuardTests
{
    [TestMethod]
    public void State_CanBeUsedOnce_UntilItExpires()
    {
        var clock = new OidcClock();
        var guards = new OidcGuards(clock);

        Assert.IsTrue(guards.TryUseState("s1", TimeSpan.FromMinutes(10)));
        Assert.IsFalse(guards.TryUseState("s1", TimeSpan.FromMinutes(10)));
        Assert.IsTrue(guards.TryUseState("s2", TimeSpan.FromMinutes(10)));

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.IsTrue(guards.TryUseState("s1", TimeSpan.FromMinutes(10)), "after the flow cookie is gone the value is no risk");
    }

    [TestMethod]
    public void State_TableStaysBounded_AndKeepsWorking()
    {
        var guards = new OidcGuards(new OidcClock());

        for (var i = 0; i < 25_000; i++)
        {
            Assert.IsTrue(guards.TryUseState("flood-" + i, TimeSpan.FromMinutes(10)));
        }

        Assert.IsFalse(guards.TryUseState("flood-24999", TimeSpan.FromMinutes(10)), "the newest values are still remembered");
    }

    [TestMethod]
    public void Callbacks_AreLimitedPerClient_PerMinute()
    {
        var clock = new OidcClock();
        var guards = new OidcGuards(clock);

        Assert.IsTrue(guards.TryAdmitCallback("a", 2));
        Assert.IsTrue(guards.TryAdmitCallback("a", 2));
        Assert.IsFalse(guards.TryAdmitCallback("a", 2));
        Assert.IsTrue(guards.TryAdmitCallback("b", 2), "another client has its own allowance");

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.IsTrue(guards.TryAdmitCallback("a", 2));
    }

    [TestMethod]
    public async Task Refresher_RunsOneCallForConcurrentCallers_AndReusesASuccessBriefly()
    {
        var clock = new OidcClock();
        var refresher = new OidcRefresher(clock);
        var calls = 0;
        var gate = new TaskCompletionSource();

        async Task<OidcRefresher.Outcome> Work()
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return new OidcRefresher.Outcome(OidcRefresher.Status.Renewed, new OidcSession(), "cookie");
        }

        var all = Enumerable.Range(0, 20).Select(_ => refresher.RunAsync("token", Work)).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(all);

        Assert.AreEqual(1, calls);
        Assert.IsTrue(results.All(static r => r.Status == OidcRefresher.Status.Renewed));

        await refresher.RunAsync("token", Work);
        Assert.AreEqual(1, calls, "a late caller with the same token gets the same answer");

        await refresher.RunAsync("other-token", Work);
        Assert.AreEqual(2, calls, "another session is not affected");

        clock.Advance(TimeSpan.FromSeconds(61));
        await refresher.RunAsync("token", Work);
        Assert.AreEqual(3, calls, "the shared answer is not kept for long");
    }

    [TestMethod]
    public async Task Refresher_RemembersAFailureOnlyBriefly()
    {
        var clock = new OidcClock();
        var refresher = new OidcRefresher(clock);
        var calls = 0;

        Task<OidcRefresher.Outcome> Work()
        {
            calls++;
            return Task.FromResult(new OidcRefresher.Outcome(OidcRefresher.Status.Unavailable, null, null));
        }

        await refresher.RunAsync("t", Work);
        await refresher.RunAsync("t", Work);
        Assert.AreEqual(1, calls);

        clock.Advance(TimeSpan.FromSeconds(6));
        await refresher.RunAsync("t", Work);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void FixedEquals_ComparesWholeValues()
    {
        Assert.IsTrue(OidcProvider.FixedEquals("abc", "abc"));
        Assert.IsFalse(OidcProvider.FixedEquals("abc", "abd"));
        Assert.IsFalse(OidcProvider.FixedEquals("abc", "abcd"));
        Assert.IsFalse(OidcProvider.FixedEquals(null, "abc"));
        Assert.IsFalse(OidcProvider.FixedEquals("abc", null));
    }
}

[TestClass]
public class OidcSettingsTests
{
    private static readonly string[] DefaultScopes = ["openid", "email", "profile"];
    private static readonly string[] RefreshScopes = ["openid", "email", "profile", "offline_access"];
    private static readonly string[] EmailOnlyScopes = ["openid", "email"];

    private static RoutePolicyConfig Policy(Func<RoutePolicyConfig, RoutePolicyConfig>? shape = null)
    {
        var policy = new RoutePolicyConfig
        {
            Name = "login",
            Kind = "oidc",
            Issuer = "https://idp.example.test",
            ClientId = "client",
            CookieSecretEnv = "KEY",
        };
        return shape is null ? policy : shape(policy);
    }

    private static string? Env(string name) => name == "KEY" ? new string('1', 64) : name == "SECRET" ? "s3cret\n" : null;

    [TestMethod]
    public void Create_AppliesDefaults()
    {
        var settings = OidcSettings.Create(Policy(), Env);

        Assert.AreEqual("/_titanium/oidc/callback", settings.RedirectPath);
        Assert.AreEqual("titanium_oidc", settings.CookieName);
        Assert.AreEqual("groups", settings.GroupsClaim);
        Assert.AreEqual(TimeSpan.FromHours(8), settings.SessionTtl);
        CollectionAssert.AreEqual(DefaultScopes, settings.Scopes);
        Assert.IsNull(settings.ClientSecret, "a public client has no secret");
    }

    [TestMethod]
    public void Create_AddsOfflineAccessForRefresh_AndAlwaysAsksForOpenid()
    {
        var refresh = OidcSettings.Create(Policy(static p => new RoutePolicyConfig
        {
            Name = p.Name, Kind = p.Kind, Issuer = p.Issuer, ClientId = p.ClientId, CookieSecretEnv = p.CookieSecretEnv, RefreshTokens = true,
        }), Env);
        var custom = OidcSettings.Create(Policy(static p => new RoutePolicyConfig
        {
            Name = p.Name, Kind = p.Kind, Issuer = p.Issuer, ClientId = p.ClientId, CookieSecretEnv = p.CookieSecretEnv, Scopes = ["email"],
        }), Env);

        CollectionAssert.AreEqual(RefreshScopes, refresh.Scopes);
        CollectionAssert.AreEqual(EmailOnlyScopes, custom.Scopes);
    }

    [TestMethod]
    public void Create_ReadsSecretsFromTheEnvironmentOrAFile_AndTrimsTheLineEnd()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, new string('2', 64) + "\r\n");
            var fromEnv = OidcSettings.Create(Policy(static p => new RoutePolicyConfig
            {
                Name = p.Name, Kind = p.Kind, Issuer = p.Issuer, ClientId = p.ClientId, CookieSecretEnv = "KEY", ClientSecretEnv = "SECRET",
            }), Env);
            var fromFile = OidcSettings.Create(new RoutePolicyConfig
            {
                Name = "login", Kind = "oidc", Issuer = "https://idp.example.test", ClientId = "client", CookieSecretFile = path,
            }, Env);

            Assert.AreEqual("s3cret", fromEnv.ClientSecret);
            Assert.IsNotNull(fromFile.Crypto);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Create_ThrowsForAMissingSecret_WithoutEchoingAnyValue()
    {
        var unset = Assert.ThrowsExactly<OidcConfigException>(() => OidcSettings.Create(Policy(static p => new RoutePolicyConfig
        {
            Name = p.Name, Kind = p.Kind, Issuer = p.Issuer, ClientId = p.ClientId, CookieSecretEnv = "NOT_SET",
        }), Env));
        var none = Assert.ThrowsExactly<OidcConfigException>(() => OidcSettings.Create(new RoutePolicyConfig
        {
            Name = "login", Kind = "oidc", Issuer = "https://idp.example.test", ClientId = "client",
        }, Env));
        var missingFile = Assert.ThrowsExactly<OidcConfigException>(() => OidcSettings.Create(new RoutePolicyConfig
        {
            Name = "login", Kind = "oidc", Issuer = "https://idp.example.test", ClientId = "client",
            CookieSecretFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        }, Env));

        StringAssert.Contains(unset.Message, "NOT_SET");
        StringAssert.Contains(none.Message, "cookieSecret");
        StringAssert.Contains(missingFile.Message, "could not be read");
    }

    [TestMethod]
    public void Allows_CombinesEmailAndGroupRules()
    {
        var settings = OidcSettings.Create(new RoutePolicyConfig
        {
            Name = "login", Kind = "oidc", Issuer = "https://idp.example.test", ClientId = "client", CookieSecretEnv = "KEY",
            AllowedEmails = ["Dana@Example.test"],
            AllowedEmailDomains = ["corp.example"],
            AllowedGroups = ["admins", "ops"],
        }, Env);

        Assert.IsTrue(settings.Allows("dana@example.test", true, ["ops"]));
        Assert.IsTrue(settings.Allows("x@CORP.example", null, ["admins"]));
        Assert.IsFalse(settings.Allows("x@corp.example", true, ["users"]));
        Assert.IsFalse(settings.Allows("x@other.example", true, ["admins"]));
        Assert.IsFalse(settings.Allows("x@corp.example", false, ["admins"]));
        Assert.IsFalse(settings.Allows(null, null, ["admins"]));
        Assert.IsFalse(settings.Allows("x@corp.example", true, []));
    }

    [TestMethod]
    public void Allows_WithNoRules_AdmitsAnyone()
    {
        var settings = OidcSettings.Create(Policy(), Env);

        Assert.IsTrue(settings.Allows(null, null, []));
    }
}

/// <summary>What an ID token must look like, checked against the provider client directly.</summary>
[TestClass]
public class OidcIdTokenValidationTests
{
    private static readonly string[] UsersGroup = ["users"];

    private static async Task<(OidcHarness Harness, OidcProvider Provider)> StartAsync()
    {
        var harness = await OidcHarness.StartAsync();
        using var browser = harness.NewBrowser();
        await OidcHarness.StartLoginAsync(browser);
        return (harness, harness.Handler!.ProviderOf(harness.Policy)!);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task AGoodToken_IsAccepted_AndTheNonceMustMatch()
    {
        var (h, provider) = await StartAsync();
        await using var _ = h;
        var token = h.Idp.IdToken("nonce-1");

        var identity = await provider.ValidateIdTokenAsync(token, "nonce-1", default);
        await Assert.ThrowsExactlyAsync<OidcTokenInvalidException>(() => provider.ValidateIdTokenAsync(token, "nonce-2", default));
        await Assert.ThrowsExactlyAsync<OidcTokenInvalidException>(() => provider.ValidateIdTokenAsync(h.Idp.IdToken(null), "nonce-1", default));

        Assert.AreEqual("user-1", identity.Subject);
        Assert.AreEqual("alice@example.test", identity.Email);
        CollectionAssert.AreEqual(UsersGroup, identity.Groups);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task AnUnsignedToken_IsRefused()
    {
        var (h, provider) = await StartAsync();
        await using var _ = h;
        var header = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\",\"kid\":\"k1\"}");
        var payload = Base64UrlEncoder.Encode(
            $"{{\"iss\":\"{h.Idp.Issuer}\",\"aud\":\"{FakeIdp.ClientId}\",\"sub\":\"root\",\"exp\":{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}");

        await Assert.ThrowsExactlyAsync<OidcTokenInvalidException>(() => provider.ValidateIdTokenAsync($"{header}.{payload}.", null, default));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ATokenSignedWithASharedSecretAlgorithm_IsRefused()
    {
        var (h, provider) = await StartAsync();
        await using var _ = h;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = h.Idp.Issuer,
            Audience = FakeIdp.ClientId,
            Claims = new Dictionary<string, object> { ["sub"] = "root" },
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "k1" }, SecurityAlgorithms.HmacSha256),
        };
        var token = new JsonWebTokenHandler().CreateToken(descriptor);

        await Assert.ThrowsExactlyAsync<OidcTokenInvalidException>(() => provider.ValidateIdTokenAsync(token, null, default));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task AChangedPayload_BreaksTheSignature()
    {
        var (h, provider) = await StartAsync();
        await using var _ = h;
        var parts = h.Idp.IdToken("n").Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace("user-1", "root", StringComparison.Ordinal);
        var changed = $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";

        await Assert.ThrowsExactlyAsync<OidcTokenInvalidException>(() => provider.ValidateIdTokenAsync(changed, "n", default));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task GarbageInput_IsRefusedWithoutThrowingAnythingElse()
    {
        var (h, provider) = await StartAsync();
        await using var _ = h;

        foreach (var garbage in new[] { string.Empty, "x", "a.b.c", "....", new string('A', 100_000) })
        {
            await Assert.ThrowsExactlyAsync<OidcTokenInvalidException>(() => provider.ValidateIdTokenAsync(garbage, null, default), garbage.Length.ToString());
        }
    }
}
