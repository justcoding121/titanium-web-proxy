using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Oidc;
using Titanium.TestKit;

namespace Titanium.Plus.Tests;

/// <summary>
/// The <c>oidc</c> route policy end to end: a browser-like client, the proxy with the Plus handler, a protected
/// origin, and a fake identity provider. Time is a shared manual clock, so expiry and refresh need no waiting.
/// </summary>
[TestClass]
public class OidcLoginIntegrationTests
{
    private const string Session = "titanium_oidc";
    private const string FlowPrefix = "titanium_oidc_f";
    private const string Callback = "/_titanium/oidc/callback";

    private static (string Name, string Value) Html => ("Accept", "text/html");

    // ------------------------------------------------------------------ the happy path

    [TestMethod]
    [Timeout(60_000)]
    public async Task FullFlow_SignsIn_AndForwardsOnlyTheProxyMadeClaimHeaders()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();

        var authorize = await h.StartLoginAsync(browser, "/app?x=1");
        var uri = new Uri(authorize);
        Assert.AreEqual(h.Idp.Issuer, uri.GetLeftPart(UriPartial.Authority));
        Assert.AreEqual("/authorize", uri.AbsolutePath);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.AreEqual("code", query["response_type"]);
        Assert.AreEqual(FakeIdp.ClientId, query["client_id"]);
        Assert.AreEqual("http://matrix.test" + Callback, query["redirect_uri"]);
        Assert.AreEqual("openid email profile", query["scope"]);
        Assert.AreEqual("S256", query["code_challenge_method"]);
        Assert.AreEqual(43, query["code_challenge"]!.Length);
        Assert.IsTrue(query["state"]!.Length >= 32 && query["nonce"]!.Length >= 32);
        Assert.AreNotEqual(query["state"], query["nonce"]);
        Assert.AreEqual(0, h.Origin.Hits, "nothing is forwarded before sign-in");

        var flow = browser.LastSetCookies.Single();
        StringAssert.StartsWith(flow, FlowPrefix);
        AssertCookieAttributes(flow);
        Assert.IsFalse(flow.Contains(query["state"]!, StringComparison.Ordinal), "the flow cookie is encrypted, not just signed");
        Assert.IsFalse(flow.Contains(query["nonce"]!, StringComparison.Ordinal));

        var callback = await h.VisitProviderAsync(browser, authorize);
        using var done = await browser.GetAsync(callback, Html);
        Assert.AreEqual(HttpStatusCode.Found, done.StatusCode);
        Assert.AreEqual("/app?x=1", done.Headers.Location!.OriginalString);
        Assert.AreEqual("no-store", done.Headers.CacheControl?.ToString());

        var session = browser.LastSetCookies.Single(static l => l.StartsWith(Session + "=", StringComparison.Ordinal));
        AssertCookieAttributes(session);
        Assert.IsTrue(session.Length < 4096, "the session cookie has to fit a browser");
        Assert.IsTrue(browser.LastSetCookies.Any(static l => l.StartsWith(FlowPrefix, StringComparison.Ordinal) && l.Contains("Max-Age=0", StringComparison.Ordinal)),
            "the flow cookie is removed once it has been used");
        CollectionAssert.AreEquivalent(new[] { Session }, browser.Cookies.Keys.ToArray());

        var exchange = h.Idp.TokenCalls.Single();
        Assert.AreEqual("authorization_code", exchange.GrantType);
        StringAssert.StartsWith(exchange.Authorization, "Basic ");
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(exchange.Form["code_verifier"])))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual(query["code_challenge"], challenge, "the verifier sent at the token endpoint matches the challenge");
        Assert.AreEqual(query["redirect_uri"], exchange.Form["redirect_uri"]);

        using var page = await browser.GetAsync("http://matrix.test/app?x=1", Html, ("X-User-Email", "forged@example.test"));
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.AreEqual("origin-ok", await page.Content.ReadAsStringAsync());
        var seen = h.Origin.Last;
        Assert.AreEqual("alice@example.test", seen.Header("X-User-Email"), "a client-sent claim header is replaced by the real claim");
        Assert.AreEqual("user-1", seen.Header("X-User-Id"));
        Assert.AreEqual("Alice Example", seen.Header("X-User-Name"));
        Assert.IsFalse((seen.Header("Cookie") ?? string.Empty).Contains(Session, StringComparison.Ordinal),
            "the origin never sees the session cookie");
        Assert.IsTrue(page.Headers.GetValues("Set-Cookie").Contains("app=1; Path=/"), "the origin's own cookies pass through");
        Assert.AreEqual(1, h.Idp.DiscoveryHits);
        Assert.AreEqual(1, h.Idp.JwksHits);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ClientCookiesOfTheSameName_AndForgedClaimHeaders_NeverReachTheOrigin()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();
        using (await h.SignInAsync(browser))
        {
        }

        browser.Cookies["theme"] = "dark";
        browser.Cookies[FlowPrefix + "abc"] = "junk";
        using var page = await browser.GetAsync("http://matrix.test/x", Html, ("X-User-Id", "root"), ("X-User-Name", "root"));

        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.AreEqual("theme=dark", h.Origin.Last.Header("Cookie"));
        Assert.AreEqual("user-1", h.Origin.Last.Header("X-User-Id"));
        Assert.AreEqual("Alice Example", h.Origin.Last.Header("X-User-Name"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CustomRedirectPathAndCookieName_Work()
    {
        await using var h = await OidcHarness.StartAsync(o =>
        {
            o.RedirectPath = "/auth/cb";
            o.CookieName = "gate";
        });
        h.Idp.RegisteredRedirectUri = "http://matrix.test/auth/cb";
        using var browser = h.NewBrowser();

        using var done = await h.SignInAsync(browser);

        Assert.AreEqual(HttpStatusCode.Found, done.StatusCode);
        CollectionAssert.AreEquivalent(new[] { "gate" }, browser.Cookies.Keys.ToArray());
        using var page = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task PublicBaseUrl_FixesTheRedirectUri_WhateverTheHostHeaderSays()
    {
        await using var h = await OidcHarness.StartAsync(o => o.PublicBaseUrl = "https://app.example.test");
        h.Idp.RegisteredRedirectUri = "https://app.example.test" + Callback;
        using var browser = h.NewBrowser();

        var authorize = await h.StartLoginAsync(browser);

        var query = HttpUtility.ParseQueryString(new Uri(authorize).Query);
        Assert.AreEqual("https://app.example.test" + Callback, query["redirect_uri"]);
        var next = await h.VisitProviderAsync(browser, authorize);
        StringAssert.StartsWith(next, "https://app.example.test" + Callback);
    }

    // ------------------------------------------------------------------ requests that cannot follow a redirect

    [DataTestMethod]
    [DataRow("Accept", "application/json")]
    [DataRow("Accept", "application/json, text/plain, */*")]
    [DataRow("X-Requested-With", "XMLHttpRequest")]
    [Timeout(60_000)]
    public async Task ApiRequest_WithoutSession_Gets401Json_NotARedirect(string header, string value)
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();

        using var response = await browser.GetAsync("http://matrix.test/api/items", (header, value));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsNull(response.Headers.Location);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "unauthenticated");
        Assert.AreEqual(0, browser.LastSetCookies.Count, "no login is started for a script");
        Assert.AreEqual(0, h.Origin.Hits);
        Assert.AreEqual(0, h.Idp.AuthorizeCalls.Count);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task BrowserNavigation_ThatAlsoAcceptsJson_StillGetsTheLoginRedirect()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();

        using var response = await browser.GetAsync("http://matrix.test/", ("Accept", "text/html,application/xhtml+xml,application/json;q=0.9"));

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task UnsafeMethod_WithoutSession_Gets401_AndIsNeverForwarded()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();

        using var response = await browser.SendAsync(HttpMethod.Post, "http://matrix.test/api/items", Html);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, h.Origin.Hits);
    }

    // ------------------------------------------------------------------ cookies

    [TestMethod]
    [Timeout(60_000)]
    public async Task TamperedSessionCookie_IsRefused_AndCleared()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();
        using (await h.SignInAsync(browser))
        {
        }

        var good = browser.Cookies[Session];
        var middle = good.Length / 2;
        browser.Cookies[Session] = good[..middle] + (good[middle] == 'A' ? 'B' : 'A') + good[(middle + 1)..];

        using var response = await browser.GetAsync("http://matrix.test/app", Html);

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        StringAssert.StartsWith(response.Headers.Location!.OriginalString, h.Idp.Issuer + "/authorize");
        Assert.IsTrue(browser.LastSetCookies.Any(static l => l.StartsWith(Session + "=;", StringComparison.Ordinal)), "the bad cookie is cleared");
        Assert.AreEqual(0, h.Origin.Hits);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ForgedSessionCookie_NeedsTheKey()
    {
        await using var h = await OidcHarness.StartAsync();
        var session = new OidcSession
        {
            Subject = "mallory",
            Email = "mallory@example.test",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            RefreshAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        };
        var rightKey = new OidcCookieCrypto(OidcCookieCrypto.ParseKeys(h.Options.CookieKeys));
        var otherKey = new OidcCookieCrypto(OidcCookieCrypto.ParseKeys(new string('7', 64)));

        using var withRightKey = h.NewBrowser();
        withRightKey.Cookies[Session] = rightKey.Seal("session", Session, session.ToUtf8());
        using var accepted = await withRightKey.GetAsync("http://matrix.test/app", Html);

        using var withWrongKey = h.NewBrowser();
        withWrongKey.Cookies[Session] = otherKey.Seal("session", Session, session.ToUtf8());
        using var wrongKey = await withWrongKey.GetAsync("http://matrix.test/app", Html);

        using var wrongPurpose = h.NewBrowser();
        wrongPurpose.Cookies[Session] = rightKey.Seal("flow", Session, session.ToUtf8());
        using var crossUse = await wrongPurpose.GetAsync("http://matrix.test/app", Html);

        using var wrongName = h.NewBrowser();
        wrongName.Cookies[Session] = rightKey.Seal("session", "another_cookie", session.ToUtf8());
        using var moved = await wrongName.GetAsync("http://matrix.test/app", Html);

        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, "the same bytes with the right key are a valid session");
        Assert.AreEqual(HttpStatusCode.Found, wrongKey.StatusCode);
        Assert.AreEqual(HttpStatusCode.Found, crossUse.StatusCode, "a cookie made for another purpose is not a session");
        Assert.AreEqual(HttpStatusCode.Found, moved.StatusCode, "a cookie cannot be moved to another name");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CookieKeyRotation_AcceptsTheOldKey_AndSealsWithTheNewOne()
    {
        await using var h = await OidcHarness.StartAsync();
        var oldKey = h.Options.CookieKeys;
        using var browser = h.NewBrowser();
        using (await h.SignInAsync(browser))
        {
        }

        var newKey = new string('9', 64);
        h.Options.CookieKeys = newKey + "," + oldKey;
        h.Handler!.Reset(h.Policy);

        using var stillValid = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.OK, stillValid.StatusCode, "a session made under the old key survives rotation");

        using var fresh = h.NewBrowser();
        using (await h.SignInAsync(fresh))
        {
        }

        var onlyNew = new OidcCookieCrypto(OidcCookieCrypto.ParseKeys(newKey));
        Assert.IsNotNull(onlyNew.Open("session", Session, fresh.Cookies[Session]), "new sessions are sealed with the first key");
        var onlyOld = new OidcCookieCrypto(OidcCookieCrypto.ParseKeys(oldKey));
        Assert.IsNull(onlyOld.Open("session", Session, fresh.Cookies[Session]));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task SessionEndsAtItsTtl()
    {
        await using var h = await OidcHarness.StartAsync(o => o.SessionTtlSeconds = 600);
        using var browser = h.NewBrowser();
        using (await h.SignInAsync(browser))
        {
        }

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        using (var early = await browser.GetAsync("http://matrix.test/app", Html))
        {
            Assert.AreEqual(HttpStatusCode.OK, early.StatusCode);
        }

        h.Clock.Advance(TimeSpan.FromMinutes(6));
        var hits = h.Origin.Hits;
        using var late = await browser.GetAsync("http://matrix.test/app", Html);

        Assert.AreEqual(HttpStatusCode.Found, late.StatusCode);
        Assert.IsTrue(browser.LastSetCookies.Any(static l => l.StartsWith(Session + "=;", StringComparison.Ordinal)));
        Assert.AreEqual(hits, h.Origin.Hits);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task LargeGroupClaim_KeepsOnlyTheGroupsARuleNames_AndStillFits()
    {
        await using var h = await OidcHarness.StartAsync(o => o.AllowedGroups = ["group-3"]);
        h.Idp.Groups = Enumerable.Range(0, 800).Select(static i => "group-" + i).ToArray();
        using var browser = h.NewBrowser();

        using var done = await h.SignInAsync(browser);

        Assert.AreEqual(HttpStatusCode.Found, done.StatusCode);
        Assert.IsTrue(browser.Cookies[Session].Length < 3000);
        using var page = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
    }

    // ------------------------------------------------------------------ expiry and what the provider says

    [DataTestMethod]
    [DataRow("audience")]
    [DataRow("issuer")]
    [DataRow("nonce")]
    [DataRow("expired")]
    [DataRow("unpublishedKey")]
    [DataRow("pkce")]
    [DataRow("denied")]
    [Timeout(60_000)]
    public async Task Callback_RefusesAnAnswerThatIsNotRight(string flaw)
    {
        await using var h = await OidcHarness.StartAsync();
        switch (flaw)
        {
            case "audience": h.Idp.AudienceOverride = "another-client"; break;
            case "issuer": h.Idp.IssuerOverride = "http://127.0.0.1:1"; break;
            case "nonce": h.Idp.NonceOverride = "not-the-nonce-we-sent"; break;
            case "expired": h.Idp.IdTokenLifetime = TimeSpan.FromMinutes(-5); break;
            case "unpublishedKey": h.Idp.SignWithUnpublishedKey = true; break;
            case "pkce": h.Idp.CorruptPkce = true; break;
            case "denied": h.Idp.DenyAtAuthorize = true; break;
        }

        using var browser = h.NewBrowser();
        using var done = await h.SignInAsync(browser);

        Assert.AreEqual(HttpStatusCode.Unauthorized, done.StatusCode, flaw);
        Assert.IsFalse(browser.Cookies.ContainsKey(Session), "no session is made from a bad answer");
        Assert.IsNull(browser.CookieStartingWith(FlowPrefix), "the flow cookie is used up");
        Assert.AreEqual(0, h.Origin.Hits);

    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task IdToken_ExpiredWithinTheSkew_IsAccepted_AndOneBeyondItIsNot()
    {
        await using var h = await OidcHarness.StartAsync();
        h.Idp.IdTokenLifetime = TimeSpan.FromSeconds(-30);
        using var inside = h.NewBrowser();
        using var accepted = await h.SignInAsync(inside);

        h.Idp.IdTokenLifetime = TimeSpan.FromSeconds(-90);
        using var outside = h.NewBrowser();
        using var refused = await h.SignInAsync(outside);

        Assert.AreEqual(HttpStatusCode.Found, accepted.StatusCode, "60 seconds of clock skew are allowed");
        Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task TokenEndpoint_IsCalledWithTheClientSecret_NotInTheForm()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();
        using (await h.SignInAsync(browser))
        {
        }

        var call = h.Idp.TokenCalls.Single();
        Assert.IsFalse(call.Form.ContainsKey("client_secret"));
        var basic = Encoding.UTF8.GetString(Convert.FromBase64String(call.Authorization!["Basic ".Length..]));
        Assert.AreEqual($"{FakeIdp.ClientId}:{FakeIdp.ClientSecret}", basic);
    }

    // ------------------------------------------------------------------ who may sign in

    [TestMethod]
    [Timeout(60_000)]
    public async Task GroupRule_DeniesAMemberOfOtherGroups_AndAdmitsTheRightOne()
    {
        await using var h = await OidcHarness.StartAsync(o => o.AllowedGroups = ["admins"]);
        h.Idp.Groups = ["users"];
        using var denied = h.NewBrowser();
        using var refused = await h.SignInAsync(denied);

        h.Idp.Groups = ["users", "admins"];
        using var allowed = h.NewBrowser();
        using var accepted = await h.SignInAsync(allowed);

        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.IsFalse(denied.Cookies.ContainsKey(Session));
        Assert.AreEqual(HttpStatusCode.Found, accepted.StatusCode);
        using var page = await allowed.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task GroupRule_ReadsTheConfiguredClaim()
    {
        await using var h = await OidcHarness.StartAsync(o =>
        {
            o.AllowedGroups = ["ops"];
            o.GroupsClaim = "roles";
        });
        h.Idp.GroupsClaimName = "roles";
        h.Idp.Groups = ["ops"];
        using var browser = h.NewBrowser();

        using var done = await h.SignInAsync(browser);

        Assert.AreEqual(HttpStatusCode.Found, done.StatusCode);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task EmailRules_AdmitListedAddressesAndDomains_AndNeverAnUnverifiedAddress()
    {
        await using var h = await OidcHarness.StartAsync(o =>
        {
            o.AllowedEmails = ["Carol@Example.test"];
            o.AllowedEmailDomains = ["corp.example"];
        });

        async Task<HttpStatusCode> TryAsync(string? email, bool verified = true)
        {
            h.Idp.Email = email;
            h.Idp.EmailVerified = verified;
            using var browser = h.NewBrowser();
            using var done = await h.SignInAsync(browser);
            return done.StatusCode;
        }

        Assert.AreEqual(HttpStatusCode.Found, await TryAsync("carol@example.test"), "an address match ignores case");
        Assert.AreEqual(HttpStatusCode.Found, await TryAsync("bob@corp.example"), "a domain match");
        Assert.AreEqual(HttpStatusCode.Forbidden, await TryAsync("alice@example.test"));
        Assert.AreEqual(HttpStatusCode.Forbidden, await TryAsync("bob@sub.corp.example"), "a domain is not a suffix match");
        Assert.AreEqual(HttpStatusCode.Forbidden, await TryAsync("bob@corp.example", verified: false));
        Assert.AreEqual(HttpStatusCode.Forbidden, await TryAsync(null));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task EmailAndGroupRules_BothHaveToHold()
    {
        await using var h = await OidcHarness.StartAsync(o =>
        {
            o.AllowedEmailDomains = ["example.test"];
            o.AllowedGroups = ["admins"];
        });
        h.Idp.Groups = ["admins"];
        h.Idp.Email = "x@other.test";
        using var wrongDomain = h.NewBrowser();
        using var first = await h.SignInAsync(wrongDomain);

        h.Idp.Email = "x@example.test";
        h.Idp.Groups = ["users"];
        using var wrongGroup = h.NewBrowser();
        using var second = await h.SignInAsync(wrongGroup);

        h.Idp.Groups = ["admins"];
        using var both = h.NewBrowser();
        using var third = await h.SignInAsync(both);

        Assert.AreEqual(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.AreEqual(HttpStatusCode.Found, third.StatusCode);
    }

    // ------------------------------------------------------------------ where the browser is sent afterwards

    [DataTestMethod]
    [DataRow("https://evil.test/steal")]
    [DataRow("//evil.test/x")]
    [DataRow("/\\evil.test")]
    [DataRow("javascript:alert(1)")]
    [DataRow("http://matrix.test@evil.test/")]
    [DataRow("https://app.example.test.evil.test/")]
    [DataRow("/ok\r\nSet-Cookie: injected=1")]
    [DataRow("data:text/html,<script>1</script>")]
    [Timeout(60_000)]
    public async Task PostLoginRedirect_NeverLeavesTheSite(string rd)
    {
        await using var h = await OidcHarness.StartAsync(o => o.Allowlist = ["https://app.example.test"]);
        using var browser = h.NewBrowser();
        var path = "/app?rd=" + Uri.EscapeDataString(rd);

        using var done = await h.SignInAsync(browser, path);

        Assert.AreEqual(HttpStatusCode.Found, done.StatusCode);
        var location = done.Headers.Location!.OriginalString;
        Assert.AreEqual(path, location, "an unsafe target is dropped and the page that was asked for is used");
        Assert.IsFalse(done.Headers.Contains("injected"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task PostLoginRedirect_HonoursASamePathAndAnAllowlistedOrigin()
    {
        await using var h = await OidcHarness.StartAsync(o => o.Allowlist = ["https://app.example.test"]);
        using var local = h.NewBrowser();
        using var toPath = await h.SignInAsync(local, "/start?rd=" + Uri.EscapeDataString("/dashboard?tab=2"));
        using var listed = h.NewBrowser();
        using var toOrigin = await h.SignInAsync(listed, "/start?rd=" + Uri.EscapeDataString("https://app.example.test/dash"));
        using var other = h.NewBrowser();
        using var unlisted = await h.SignInAsync(other, "/start?rd=" + Uri.EscapeDataString("https://app.example.test:8443/dash"));

        Assert.AreEqual("/dashboard?tab=2", toPath.Headers.Location!.OriginalString);
        Assert.AreEqual("https://app.example.test/dash", toOrigin.Headers.Location!.OriginalString);
        StringAssert.StartsWith(unlisted.Headers.Location!.OriginalString, "/start?rd=", "another port is another origin");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task PostLoginRedirect_CollapsesAPathThatLooksLikeAnotherHost()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();

        using var done = await h.SignInAsync(browser, "//evil.test/x");

        var location = done.Headers.Location!.OriginalString;
        Assert.AreEqual("/evil.test/x", location);
    }

    // ------------------------------------------------------------------ the callback is not a way around the login

    [TestMethod]
    [Timeout(60_000)]
    public async Task Callback_WithoutTheFlowCookie_IsRefused()
    {
        await using var h = await OidcHarness.StartAsync();
        using var victim = h.NewBrowser();
        var authorize = await h.StartLoginAsync(victim);
        var callback = await h.VisitProviderAsync(victim, authorize);

        using var attacker = h.NewBrowser();
        using var response = await attacker.GetAsync(callback, Html);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, "a callback built elsewhere has no cookie to match its state");
        Assert.AreEqual(0, h.Idp.TokenCalls.Count, "the code is never redeemed");
        Assert.IsFalse(attacker.Cookies.ContainsKey(Session));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Callback_WithAnotherFlowsState_IsRefused()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();
        var first = await h.StartLoginAsync(browser, "/one");
        var second = await h.StartLoginAsync(browser, "/two");
        var callback = await h.VisitProviderAsync(browser, first);
        var otherState = HttpUtility.ParseQueryString(new Uri(second).Query)["state"]!;
        var mixed = callback[..callback.IndexOf("state=", StringComparison.Ordinal)] + "state=" + Uri.EscapeDataString(otherState);

        using var response = await browser.GetAsync(mixed, Html);

        Assert.AreNotEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.IsFalse(browser.Cookies.ContainsKey(Session), "a code from one login cannot finish another");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Callback_WithAForgedState_IsRefused()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();
        await h.StartLoginAsync(browser);

        using var response = await browser.GetAsync($"http://matrix.test{Callback}?code=abc&state=forged", Html);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, h.Idp.TokenCalls.Count);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Callback_Replayed_IsRefused_AndTheCodeIsNotRedeemedTwice()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();
        var authorize = await h.StartLoginAsync(browser);
        var callback = await h.VisitProviderAsync(browser, authorize);
        var flowCookies = browser.Cookies.ToDictionary(static c => c.Key, static c => c.Value);
        using (var first = await browser.GetAsync(callback, Html))
        {
            Assert.AreEqual(HttpStatusCode.Found, first.StatusCode);
        }

        browser.Cookies.Clear();
        foreach (var cookie in flowCookies)
        {
            browser.Cookies[cookie.Key] = cookie.Value;
        }

        using var replay = await browser.GetAsync(callback, Html);

        Assert.AreEqual(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.AreEqual(1, h.Idp.TokenCalls.Count, "the replay never reaches the token endpoint");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Callback_IsRateLimitedPerClient()
    {
        await using var h = await OidcHarness.StartAsync();
        h.Handler!.CallbackLimitPerMinute = 3;
        using var browser = h.NewBrowser();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 5; i++)
        {
            using var response = await browser.GetAsync($"http://matrix.test{Callback}?code=a&state=s{i}", Html);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.AreEqual("60", response.Headers.GetValues("Retry-After").Single());
            }
        }

        CollectionAssert.AreEqual(
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests },
            statuses);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        using var later = await browser.GetAsync($"http://matrix.test{Callback}?code=a&state=again", Html);
        Assert.AreEqual(HttpStatusCode.BadRequest, later.StatusCode, "the window passes");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Callback_OnlyAnswersGet()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();

        using var response = await browser.SendAsync(HttpMethod.Post, $"http://matrix.test{Callback}?code=a&state=b", Html);

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.AreEqual(0, h.Origin.Hits);
    }

    // ------------------------------------------------------------------ silent refresh

    [TestMethod]
    [Timeout(60_000)]
    public async Task Refresh_RenewsTheSessionQuietly_WhenTheIdTokenIsAboutToExpire()
    {
        await using var h = await OidcHarness.StartAsync(o => o.Refresh = true);
        h.Idp.IdTokenLifetime = TimeSpan.FromMinutes(10);
        using var browser = h.NewBrowser();
        var authorize = await h.StartLoginAsync(browser);
        StringAssert.Contains(HttpUtility.ParseQueryString(new Uri(authorize).Query)["scope"]!, "offline_access");
        var callback = await h.VisitProviderAsync(browser, authorize);
        using (await browser.GetAsync(callback, Html))
        {
        }

        var before = browser.Cookies[Session];
        using (var calm = await browser.GetAsync("http://matrix.test/app", Html))
        {
            Assert.AreEqual(HttpStatusCode.OK, calm.StatusCode);
        }

        Assert.AreEqual(0, h.Idp.RefreshCalls, "no refresh while the ID token is fresh");
        Assert.IsFalse(browser.LastSetCookies.Any(static l => l.StartsWith(Session + "=", StringComparison.Ordinal)));

        h.Clock.Advance(TimeSpan.FromMinutes(9.5));
        using var renewed = await browser.GetAsync("http://matrix.test/app", Html);

        Assert.AreEqual(HttpStatusCode.OK, renewed.StatusCode);
        Assert.AreEqual(1, h.Idp.RefreshCalls);
        var line = browser.LastSetCookies.Single(static l => l.StartsWith(Session + "=", StringComparison.Ordinal));
        AssertCookieAttributes(line);
        Assert.AreNotEqual(before, browser.Cookies[Session], "the cookie is replaced");
        Assert.IsTrue(browser.LastSetCookies.Contains("app=1; Path=/"), "the origin's cookie is kept alongside");
        var grant = h.Idp.TokenCalls.Last();
        Assert.AreEqual("refresh_token", grant.GrantType);
        StringAssert.StartsWith(grant.Form["refresh_token"], "rt-");

        using var after = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.OK, after.StatusCode);
        Assert.AreEqual(1, h.Idp.RefreshCalls, "the new session is fresh again");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Refresh_Refused_SendsTheBrowserBackToLogin_AndAScriptTo401()
    {
        await using var h = await OidcHarness.StartAsync(o => o.Refresh = true);
        h.Idp.IdTokenLifetime = TimeSpan.FromMinutes(10);
        using var browser = h.NewBrowser();
        using (await h.SignInAsync(browser))
        {
        }

        h.Idp.RejectRefresh = true;
        h.Clock.Advance(TimeSpan.FromMinutes(9.5));
        var cookie = browser.Cookies[Session];
        var hits = h.Origin.Hits;

        using var navigation = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.Found, navigation.StatusCode);
        StringAssert.StartsWith(navigation.Headers.Location!.OriginalString, h.Idp.Issuer + "/authorize");
        Assert.IsTrue(browser.LastSetCookies.Any(static l => l.StartsWith(Session + "=;", StringComparison.Ordinal)));

        browser.Cookies[Session] = cookie;
        using var script = await browser.GetAsync("http://matrix.test/api", ("Accept", "application/json"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, script.StatusCode);
        Assert.AreEqual(hits, h.Origin.Hits);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Refresh_WhileTheProviderIsDown_KeepsTheSessionOnlyUntilTheIdTokenIsOldEnough()
    {
        await using var h = await OidcHarness.StartAsync(o => o.Refresh = true);
        h.Idp.IdTokenLifetime = TimeSpan.FromMinutes(10);
        using var browser = h.NewBrowser();
        using (await h.SignInAsync(browser))
        {
        }

        h.Idp.Down = true;
        h.Clock.Advance(TimeSpan.FromMinutes(9.5));
        using (var tolerated = await browser.GetAsync("http://matrix.test/app", Html))
        {
            Assert.AreEqual(HttpStatusCode.OK, tolerated.StatusCode, "a short outage does not sign anyone out");
        }

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        using var stale = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, stale.StatusCode);
        Assert.IsTrue(stale.Headers.Contains("Retry-After"));

        h.Idp.Down = false;
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        using var recovered = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Refresh_ConcurrentRequests_ShareOneCallToTheProvider()
    {
        await using var h = await OidcHarness.StartAsync(o => o.Refresh = true);
        h.Idp.IdTokenLifetime = TimeSpan.FromMinutes(10);
        h.Idp.RotateRefreshToken = true;
        h.Idp.RefreshDelay = TimeSpan.FromMilliseconds(400);
        using var origin = h.NewBrowser();
        using (await h.SignInAsync(origin))
        {
        }

        var cookie = origin.Cookies[Session];
        h.Clock.Advance(TimeSpan.FromMinutes(9.5));
        var browsers = Enumerable.Range(0, 12).Select(_ =>
        {
            var b = h.NewBrowser();
            b.Cookies[Session] = cookie;
            return b;
        }).ToList();
        try
        {
            var responses = await Task.WhenAll(browsers.Select(async b =>
            {
                using var response = await b.GetAsync("http://matrix.test/app", Html);
                return response.StatusCode;
            }));

            CollectionAssert.AreEqual(Enumerable.Repeat(HttpStatusCode.OK, 12).ToArray(), responses);
            Assert.AreEqual(1, h.Idp.RefreshCalls, "a rotating refresh token can only be redeemed once, so the calls are coalesced");

            var late = h.NewBrowser();
            browsers.Add(late);
            late.Cookies[Session] = cookie;
            using var again = await late.GetAsync("http://matrix.test/app", Html);
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, "a request still carrying the old cookie gets the same answer");
            Assert.AreEqual(1, h.Idp.RefreshCalls);
        }
        finally
        {
            browsers.ForEach(static b => b.Dispose());
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task NoRefreshToken_FromTheProvider_MeansTheSessionEndsWithTheIdToken()
    {
        await using var h = await OidcHarness.StartAsync(o => o.Refresh = true);
        h.Idp.IdTokenLifetime = TimeSpan.FromMinutes(10);
        h.Idp.IssueRefreshToken = false;
        using var browser = h.NewBrowser();
        using (await h.SignInAsync(browser))
        {
        }

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        using (var early = await browser.GetAsync("http://matrix.test/app", Html))
        {
            Assert.AreEqual(HttpStatusCode.OK, early.StatusCode);
        }

        h.Clock.Advance(TimeSpan.FromMinutes(6));
        using var late = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.Found, late.StatusCode);
        Assert.AreEqual(0, h.Idp.RefreshCalls);
    }

    // ------------------------------------------------------------------ sign-out

    [TestMethod]
    [Timeout(60_000)]
    public async Task Logout_ClearsTheSession_AndIsNotForwarded()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();
        using (await h.SignInAsync(browser))
        {
        }

        var hits = h.Origin.Hits;
        using var response = await browser.GetAsync("http://matrix.test/logout?rd=" + Uri.EscapeDataString("/bye"), Html);

        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.AreEqual("/bye", response.Headers.Location!.OriginalString);
        Assert.IsTrue(browser.LastSetCookies.Any(static l => l.StartsWith(Session + "=;", StringComparison.Ordinal) && l.Contains("Max-Age=0", StringComparison.Ordinal)));
        Assert.AreEqual(0, browser.Cookies.Count);
        Assert.AreEqual(hits, h.Origin.Hits);

        using var after = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.Found, after.StatusCode);
        StringAssert.StartsWith(after.Headers.Location!.OriginalString, h.Idp.Issuer + "/authorize");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Logout_WithAnOpenRedirect_GoesToTheRootInstead()
    {
        await using var h = await OidcHarness.StartAsync();
        using var browser = h.NewBrowser();

        using var response = await browser.GetAsync("http://matrix.test/logout?rd=" + Uri.EscapeDataString("https://evil.test/"), Html);

        Assert.AreEqual("/", response.Headers.Location!.OriginalString);
    }

    // ------------------------------------------------------------------ the provider's own keys and documents

    [TestMethod]
    [Timeout(60_000)]
    public async Task KeyRotation_AnUnknownKeyIdRefetchesTheKeys_ButNotMoreOftenThanEvery30Seconds()
    {
        await using var h = await OidcHarness.StartAsync();
        using var first = h.NewBrowser();
        using (var done = await h.SignInAsync(first))
        {
            Assert.AreEqual(HttpStatusCode.Found, done.StatusCode);
        }

        Assert.AreEqual(1, h.Idp.JwksHits);
        h.Idp.RotateKeys();

        using var tooSoon = h.NewBrowser();
        using (var refused = await h.SignInAsync(tooSoon))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode, "a flood of unknown key ids cannot make the proxy hammer the provider");
        }

        Assert.AreEqual(1, h.Idp.JwksHits);
        h.Clock.Advance(TimeSpan.FromSeconds(31));

        using var later = h.NewBrowser();
        using var accepted = await h.SignInAsync(later);

        Assert.AreEqual(HttpStatusCode.Found, accepted.StatusCode);
        Assert.AreEqual(2, h.Idp.JwksHits);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task KeyRotation_AKeyDroppedFromJwks_IsRejectedAfterRefresh()
    {
        await using var h = await OidcHarness.StartAsync();
        using var first = h.NewBrowser();
        using (var accepted = await h.SignInAsync(first))
        {
            Assert.AreEqual(HttpStatusCode.Found, accepted.StatusCode);
        }

        h.Idp.RotateKeys();
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        using var overlap = h.NewBrowser();
        using (var stillOk = await h.SignInAsync(overlap))
        {
            Assert.AreEqual(HttpStatusCode.Found, stillOk.StatusCode, "both keys published: new kid works");
        }

        h.Idp.RevokeInactiveKeys();
        h.Idp.SignWithRevokedKey = true;
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        using var other = h.NewBrowser();
        using var refused = await h.SignInAsync(other);
        Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode,
            "a kid dropped from JWKS must fail once the proxy refreshes");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Keys_RefreshFailure_FailsClosedWith503()
    {
        await using var h = await OidcHarness.StartAsync();
        using var first = h.NewBrowser();
        using (var done = await h.SignInAsync(first))
        {
            Assert.AreEqual(HttpStatusCode.Found, done.StatusCode);
        }

        h.Idp.JwksDown = true;
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        using var second = h.NewBrowser();
        using var refused = await h.SignInAsync(second);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.StatusCode,
            "a JWKS outage must not keep validating with keys that may already have been rotated away");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Discovery_IsCached_RefreshedAfterAnHour_AndKeptWhenTheProviderFails()
    {
        await using var h = await OidcHarness.StartAsync();
        using var one = h.NewBrowser();
        await h.StartLoginAsync(one);
        using var two = h.NewBrowser();
        await h.StartLoginAsync(two);
        Assert.AreEqual(1, h.Idp.DiscoveryHits, "the second login uses the cached document");

        h.Clock.Advance(TimeSpan.FromMinutes(61));
        using var three = h.NewBrowser();
        await h.StartLoginAsync(three);
        Assert.AreEqual(2, h.Idp.DiscoveryHits);

        h.Idp.Down = true;
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        using var four = h.NewBrowser();
        using var stillRedirects = await four.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.Found, stillRedirects.StatusCode, "the last good document is used while the provider is down");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Discovery_Failure_Gives503_ThenRecoversAfterTheBackoff()
    {
        await using var h = await OidcHarness.StartAsync();
        h.Idp.Down = true;
        using var browser = h.NewBrowser();

        using var down = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.IsTrue(down.Headers.Contains("Retry-After"));

        h.Idp.Down = false;
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        using var recovered = await browser.GetAsync("http://matrix.test/app", Html);
        Assert.AreEqual(HttpStatusCode.Found, recovered.StatusCode);
    }

    // ------------------------------------------------------------------ failing closed

    [TestMethod]
    [Timeout(60_000)]
    public async Task WithoutAPolicyHandler_TheRouteFailsClosed()
    {
        await using var h = await OidcHarness.StartAsync(registerHandler: false);
        using var browser = h.NewBrowser();

        using var response = await browser.GetAsync("http://matrix.test/app", Html);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(0, h.Origin.Hits, "a route that asks for a login is never served without one");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task AnUnsetSecret_FailsClosed_UntilTheConfigIsFixed()
    {
        await using var h = await OidcHarness.StartAsync(o => o.MissingClientSecret = true);
        using var browser = h.NewBrowser();

        using var response = await browser.GetAsync("http://matrix.test/app", Html);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(0, h.Origin.Hits);
        Assert.AreEqual(0, h.Idp.AuthorizeCalls.Count);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ACookieKeyOfTheWrongSize_FailsClosed()
    {
        await using var h = await OidcHarness.StartAsync(o => o.CookieKeys = "too-short");
        using var browser = h.NewBrowser();

        using var response = await browser.GetAsync("http://matrix.test/app", Html);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(0, h.Origin.Hits);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertCookieAttributes(string setCookie)
    {
        StringAssert.Contains(setCookie, "; HttpOnly");
        StringAssert.Contains(setCookie, "; Secure");
        StringAssert.Contains(setCookie, "; SameSite=Lax");
        StringAssert.Contains(setCookie, "; Path=/");
        StringAssert.Contains(setCookie, "; Max-Age=");
    }
}
