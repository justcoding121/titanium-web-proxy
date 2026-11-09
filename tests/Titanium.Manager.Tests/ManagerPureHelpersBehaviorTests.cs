using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Api;
using Titanium.Manager.Auth;
using Titanium.Manager.Validation;

namespace Titanium.Manager.Tests;

/// <summary>
///     Pure Manager helpers: YAML diff, token hashing, role parse, revision secret rejection.
///     Bug classes: CR/LF diffs diverge, token compare is length-oracle, inline secrets slip past the validator.
/// </summary>
[TestClass]
public class ManagerPureHelpersBehaviorTests
{
    [TestMethod]
    public void YamlDiff_MarksAdditionsRemovalsAndSharedLines()
    {
        // Bug class: CR/LF-only edits produce a huge noisy diff, or shared lines are marked changed.
        var from = "a\r\nb\r\nc\r\n";
        var to = "a\nb\nX\n";
        var diff = YamlDiff.Unified(from, to, "old", "new");
        StringAssert.StartsWith(diff, "--- old\n+++ new\n");
        StringAssert.Contains(diff, " a\n");
        StringAssert.Contains(diff, " b\n");
        StringAssert.Contains(diff, "-c\n");
        StringAssert.Contains(diff, "+X\n");
    }

    [TestMethod]
    public void YamlDiff_NullInputs_AreTreatedAsEmpty()
    {
        var diff = YamlDiff.Unified(null!, "line");
        StringAssert.Contains(diff, "+line\n");
        Assert.IsFalse(diff.Split('\n').Any(static l => l.StartsWith('-') && l.Length > 1 && !l.StartsWith("---", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TokenHasher_HashesMatchesAndMintsUrlSafeTokens()
    {
        var hash = TokenHasher.Hash("secret-token");
        Assert.AreEqual(64, hash.Length);
        Assert.IsTrue(TokenHasher.Matches("secret-token", hash));
        Assert.IsFalse(TokenHasher.Matches("other", hash));
        Assert.IsFalse(TokenHasher.Matches("", hash));
        Assert.IsFalse(TokenHasher.Matches("secret-token", ""));
        Assert.ThrowsExactly<ArgumentException>(() => TokenHasher.Hash(""));

        var token = TokenHasher.NewToken();
        Assert.IsFalse(string.IsNullOrWhiteSpace(token));
        Assert.IsFalse(token.Contains('+', StringComparison.Ordinal));
        Assert.IsFalse(token.Contains('/', StringComparison.Ordinal));
        Assert.IsFalse(token.Contains('=', StringComparison.Ordinal));
    }

    [TestMethod]
    public void AdminRoleParser_RoundTripsKnownRoles()
    {
        Assert.IsTrue(AdminRoleParser.TryParse("viewer", out var viewer));
        Assert.AreEqual(AdminRole.Viewer, viewer);
        Assert.IsTrue(AdminRoleParser.TryParse("EDITOR", out var editor));
        Assert.AreEqual(AdminRole.Editor, editor);
        Assert.IsTrue(AdminRoleParser.TryParse("Admin", out var admin));
        Assert.AreEqual(AdminRole.Admin, admin);

        Assert.IsFalse(AdminRoleParser.TryParse(null, out _));
        Assert.IsFalse(AdminRoleParser.TryParse(" ", out _));
        Assert.IsFalse(AdminRoleParser.TryParse("root", out _));

        Assert.AreEqual("viewer", AdminRoleParser.ToApi(AdminRole.Viewer));
        Assert.AreEqual("editor", AdminRoleParser.ToApi(AdminRole.Editor));
        Assert.AreEqual("admin", AdminRoleParser.ToApi(AdminRole.Admin));
    }

    [TestMethod]
    public void ConfigRevisionValidator_RejectsAdminTokenAndPlusOptionSecrets()
    {
        var adminToken = ConfigRevisionValidator.ValidateYaml("""
            server:
              admin:
                authToken: literal
            listeners:
              - port: 8000
                type: explicit
            """);
        Assert.IsTrue(adminToken.Any(e => e.Contains("server.admin.authToken", StringComparison.Ordinal)),
            string.Join("; ", adminToken));

        var oidc = ConfigRevisionValidator.ValidateYaml("""
            plus:
              enabled: true
              controlPlane:
                host: 127.0.0.1
                allowInsecureNonLoopback: true
                dashboardOidc:
                  issuer: https://idp.example.test
                  clientId: c
                  clientSecret: literal
                  cookieSecret: literal
                  redirectUri: https://app.example.test/cb
                  allowedEmails: [a@example.test]
            listeners:
              - port: 8000
                type: explicit
            """);
        Assert.IsTrue(oidc.Any(e => e.Contains("dashboardOidc.clientSecret", StringComparison.Ordinal)), string.Join("; ", oidc));
        Assert.IsTrue(oidc.Any(e => e.Contains("dashboardOidc.cookieSecret", StringComparison.Ordinal)), string.Join("; ", oidc));

        var options = ConfigRevisionValidator.ValidateYaml("""
            plus:
              enabled: true
              options:
                security.apiKeys: k1
                state.redis: redis://x
            listeners:
              - port: 8000
                type: explicit
            """);
        Assert.IsTrue(options.Any(e => e.Contains("plus.options.security.apiKeys", StringComparison.Ordinal)), string.Join("; ", options));
        Assert.IsTrue(options.Any(e => e.Contains("plus.options.state.redis", StringComparison.Ordinal)), string.Join("; ", options));
    }

    [TestMethod]
    public void ConfigRevisionValidator_NullYaml_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ConfigRevisionValidator.ValidateYaml(null!));
    }
}
