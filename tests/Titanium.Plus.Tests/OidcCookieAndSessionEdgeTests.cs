using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Oidc;
using Titanium.Web.Proxy.Http;

namespace Titanium.Plus.Tests;

/// <summary>
///     OIDC cookie jar and session blob leftovers that unit tests rarely hit in isolation.
///     Bug classes: origin sees session cookies, oversize values are accepted, bad session JSON becomes a session.
/// </summary>
[TestClass]
public class OidcCookieAndSessionEdgeTests
{
    private static readonly string[] ExpectedOidcCookieValues = ["one", "two"];
    private static readonly string[] ExpectedOpsGroup = ["ops"];

    [TestMethod]
    public void Values_ReadsEveryMatchingCookie_AcrossHeaderLines()
    {
        var request = new Request();
        request.Headers.AddHeader("Cookie", "a=1; titanium_oidc=one; keep=yes");
        request.Headers.AddHeader("Cookie", "titanium_oidc=two; a=2");

        var values = OidcCookies.Values(request, "titanium_oidc");
        CollectionAssert.AreEqual(ExpectedOidcCookieValues, values);
        Assert.AreEqual(0, OidcCookies.Values(request, "missing").Count);
        Assert.AreEqual(0, OidcCookies.Values(new Request(), "titanium_oidc").Count);
    }

    [TestMethod]
    public void RemoveOwn_StripsSessionAndFlowCookies_KeepsTheRest()
    {
        // Bug class: session / flow cookies are forwarded to the origin.
        var request = new Request();
        request.Headers.AddHeader("Cookie", "keep=1; titanium_oidc=s; titanium_oidc_flow_a=f; other=2");

        OidcCookies.RemoveOwn(request, "titanium_oidc", "titanium_oidc_flow_");

        var remaining = request.Headers.GetHeaders("Cookie");
        Assert.IsNotNull(remaining);
        Assert.AreEqual(1, remaining.Count);
        StringAssert.Contains(remaining[0].Value, "keep=1");
        StringAssert.Contains(remaining[0].Value, "other=2");
        Assert.IsFalse(remaining[0].Value.Contains("titanium_oidc", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RemoveOwn_WithOnlyOwnCookies_DropsTheCookieHeader()
    {
        var request = new Request();
        request.Headers.AddHeader("Cookie", "titanium_oidc=s; titanium_oidc_flow_x=f");

        OidcCookies.RemoveOwn(request, "titanium_oidc", "titanium_oidc_flow_");

        Assert.IsNull(request.Headers.GetHeaders("Cookie"));
    }

    [TestMethod]
    public void RemoveOwn_LeavesUnrelatedCookiesAlone()
    {
        var request = new Request();
        request.Headers.AddHeader("Cookie", "session=app; theme=dark");

        OidcCookies.RemoveOwn(request, "titanium_oidc", "titanium_oidc_flow_");

        var remaining = request.Headers.GetHeaders("Cookie");
        Assert.IsNotNull(remaining);
        Assert.AreEqual("session=app; theme=dark", remaining[0].Value);
    }

    [TestMethod]
    public void SetAndClear_EmitBrowserSafeAttributes()
    {
        var set = OidcCookies.Set("titanium_oidc", "abc", TimeSpan.FromSeconds(90));
        StringAssert.StartsWith(set, "titanium_oidc=abc; Path=/; Max-Age=90; HttpOnly; Secure; SameSite=Lax");

        var clear = OidcCookies.Clear("titanium_oidc");
        StringAssert.StartsWith(clear, "titanium_oidc=; Path=/; Max-Age=0; Expires=Thu, 01 Jan 1970");
        StringAssert.Contains(clear, "HttpOnly; Secure; SameSite=Lax");
    }

    [TestMethod]
    public void Fits_RejectsValuesOverTheJarBudget()
    {
        Assert.IsTrue(OidcCookies.Fits("n", "v"));
        var oversize = new string('x', OidcSettings.MaxCookieBytes);
        Assert.IsFalse(OidcCookies.Fits("n", oversize));
    }

    [TestMethod]
    public void Session_RoundTripsUtf8_AndRefusesGarbage()
    {
        var session = new OidcSession
        {
            Subject = "u1",
            Email = "u1@example.test",
            Groups = ["ops"],
            Claims = new Dictionary<string, string>(StringComparer.Ordinal) { ["role"] = "admin" },
            ExpiresAt = 1_700_000_000,
            RefreshAt = 1_699_000_000,
            RefreshToken = "rt",
        };

        var parsed = OidcSession.Parse(session.ToUtf8());
        Assert.IsNotNull(parsed);
        Assert.AreEqual("u1", parsed.Subject);
        Assert.AreEqual("u1@example.test", parsed.Email);
        CollectionAssert.AreEqual(ExpectedOpsGroup, parsed.Groups);
        Assert.AreEqual("admin", parsed.Claims["role"]);
        Assert.AreEqual(1_700_000_000, parsed.ExpiresAt);
        Assert.AreEqual("rt", parsed.RefreshToken);

        Assert.IsNull(OidcSession.Parse("{not-json"u8));
        Assert.IsNull(OidcSession.Parse("[]"u8));

        var nullFields = OidcSession.Parse("""{"sub":"x"}"""u8);
        Assert.IsNotNull(nullFields);
        Assert.IsNotNull(nullFields.Claims);
        Assert.IsNotNull(nullFields.Groups);
        Assert.AreEqual(0, nullFields.Groups.Count);
    }

    [TestMethod]
    public void Session_Parse_EmptyObject_YieldsDefaults()
    {
        var parsed = OidcSession.Parse("{}"u8);
        Assert.IsNotNull(parsed);
        Assert.IsNull(parsed.Subject);
        Assert.AreEqual(0, parsed.ExpiresAt);
        Assert.AreEqual(0, parsed.Claims.Count);
    }
}
