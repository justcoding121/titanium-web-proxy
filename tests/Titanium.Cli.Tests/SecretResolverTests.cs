using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

/// <summary><c>*Env</c> and <c>*File</c> references resolve once, name the setting on failure, and never echo a value.</summary>
[TestClass]
public partial class SecretResolverTests
{
    [GeneratedRegex("idp-client-secret")]
    private static partial Regex IdpClientSecretPattern();

    [GeneratedRegex("0123456789abcdef0123456789abcdef")]
    private static partial Regex HexKeyPattern();
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        SecretRedactor.Clear();
        _dir = Path.Combine(Path.GetTempPath(), "twp-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SecretRedactor.Clear();
        Directory.Delete(_dir, recursive: true);
    }

    private string WriteSecret(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    [TestMethod]
    public void ControlPlaneSecret_FromEnvironment()
    {
        var config = new TwpConfig
        {
            Plus = new PlusConfig { ControlPlane = new ControlPlaneConfig { SharedSecretEnv = "CP_SECRET" } },
        };

        var errors = SecretResolver.Resolve(config, Env(("CP_SECRET", "env-shared-secret")));

        Assert.AreEqual(0, errors.Count);
        Assert.AreEqual("env-shared-secret", config.Plus!.ControlPlane!.SharedSecret);
        Assert.IsNull(config.Plus.ControlPlane.SharedSecretEnv);
        Assert.AreEqual("env-shared-secret", RunCommand.BuildPlusOptions(config.Plus)["controlPlane.sharedSecret"]);
    }

    [TestMethod]
    public void ControlPlaneSecret_FromFile_DropsTheTrailingNewline()
    {
        var path = WriteSecret("shared", "file-shared-secret\n");
        var config = new TwpConfig
        {
            Plus = new PlusConfig { ControlPlane = new ControlPlaneConfig { SharedSecretFile = path } },
        };

        var errors = SecretResolver.Resolve(config);

        Assert.AreEqual(0, errors.Count);
        Assert.AreEqual("file-shared-secret", config.Plus!.ControlPlane!.SharedSecret);
    }

    [TestMethod]
    public void RelativeSecretFile_ResolvesAgainstTheWorkingDirectory()
    {
        WriteSecret("rel-token", "relative-token");
        var config = new TwpConfig
        {
            Server = new ServerConfig { Admin = new AdminConfig { Enabled = true, AuthTokenFile = "rel-token" } },
        };
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_dir);
        try
        {
            Assert.AreEqual(0, SecretResolver.Resolve(config).Count);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.AreEqual("relative-token", config.Server.Admin.AuthToken);
    }

    [TestMethod]
    public void PlusOptions_SiblingsReplaceTheSensitiveKeyAndAreRemoved()
    {
        var keysFile = WriteSecret("keys", "key-one\nkey-two\r\n\r\n");
        var config = new TwpConfig
        {
            Plus = new PlusConfig
            {
                Options = new Dictionary<string, string>
                {
                    ["security.apiKeysFile"] = keysFile,
                    ["security.basicUsersEnv"] = "BASIC",
                    ["state.redisFile"] = WriteSecret("redis", "redis://:pw-redis@cache:6379\n"),
                    ["cache.enable"] = "true",
                },
            },
        };

        var errors = SecretResolver.Resolve(config, Env(("BASIC", "alice:pw-alice,bob:pw-bob")));

        Assert.AreEqual(0, errors.Count);
        var options = config.Plus!.Options!;
        Assert.AreEqual("key-one,key-two", options["security.apiKeys"]);
        Assert.AreEqual("alice:pw-alice,bob:pw-bob", options["security.basicUsers"]);
        Assert.AreEqual("redis://:pw-redis@cache:6379", options["state.redis"]);
        Assert.AreEqual("true", options["cache.enable"]);
        Assert.IsFalse(options.Keys.Any(k => k.EndsWith("Env", StringComparison.Ordinal) || k.EndsWith("File", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LiteralValues_AreLeftAlone_AndNeverInterpolated()
    {
        var config = new TwpConfig
        {
            Plus = new PlusConfig
            {
                ControlPlane = new ControlPlaneConfig { SharedSecret = "${HOME}-literal" },
                Options = new Dictionary<string, string> { ["security.apiKeys"] = "$(whoami),${USER}" },
            },
        };

        var errors = SecretResolver.Resolve(config, Env(("HOME", "should-not-appear"), ("USER", "nor-this")));

        Assert.AreEqual(0, errors.Count);
        Assert.AreEqual("${HOME}-literal", config.Plus!.ControlPlane!.SharedSecret);
        Assert.AreEqual("$(whoami),${USER}", config.Plus.Options!["security.apiKeys"]);
    }

    [TestMethod]
    public void MissingOrEmptyReferences_ReportTheSettingAndTheName_NotAValue()
    {
        var empty = WriteSecret("empty", "\n");
        var config = new TwpConfig
        {
            Plus = new PlusConfig
            {
                ControlPlane = new ControlPlaneConfig { SharedSecretEnv = "UNSET_VAR" },
                Options = new Dictionary<string, string>
                {
                    ["security.apiKeysFile"] = Path.Combine(_dir, "does-not-exist"),
                    ["state.redisFile"] = empty,
                    ["security.basicUsersEnv"] = "BLANK",
                },
            },
        };

        var errors = SecretResolver.Resolve(config, Env(("BLANK", string.Empty)));

        Assert.AreEqual(4, errors.Count, string.Join("\n", errors));
        Assert.IsTrue(errors.Any(e => e.Contains("plus.controlPlane.sharedSecretEnv 'UNSET_VAR' is not set", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("plus.options.security.apiKeysFile", StringComparison.Ordinal) && e.Contains("could not be read", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("plus.options.state.redisFile", StringComparison.Ordinal) && e.Contains("is empty", StringComparison.Ordinal)));
        Assert.IsTrue(errors.Any(e => e.Contains("plus.options.security.basicUsersEnv 'BLANK' is not set", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DashboardViewerSecret_FromEnvironmentAndFile_ReachesThePlusOptions()
    {
        var fromEnv = new TwpConfig
        {
            Plus = new PlusConfig { ControlPlane = new ControlPlaneConfig { DashboardViewerSecretEnv = "VIEWER" } },
        };
        Assert.AreEqual(0, SecretResolver.Resolve(fromEnv, Env(("VIEWER", "viewer-from-env"))).Count);
        Assert.AreEqual("viewer-from-env", fromEnv.Plus!.ControlPlane!.DashboardViewerSecret);
        Assert.IsNull(fromEnv.Plus.ControlPlane.DashboardViewerSecretEnv);
        Assert.AreEqual("viewer-from-env", RunCommand.BuildPlusOptions(fromEnv.Plus)["controlPlane.dashboardViewerSecret"]);

        var path = WriteSecret("viewer", "viewer-from-file\n");
        var fromFile = new TwpConfig
        {
            Plus = new PlusConfig { ControlPlane = new ControlPlaneConfig { DashboardViewerSecretFile = path } },
        };
        Assert.AreEqual(0, SecretResolver.Resolve(fromFile).Count);
        Assert.AreEqual("viewer-from-file", fromFile.Plus!.ControlPlane!.DashboardViewerSecret);
        CollectionAssert.Contains(SecretResolver.SecretFiles(new TwpConfig
        {
            Plus = new PlusConfig { ControlPlane = new ControlPlaneConfig { DashboardViewerSecretFile = path } },
        }).ToArray(), path);
    }

    [TestMethod]
    public void DashboardViewerSecret_UnsetEnvironmentVariable_NamesTheSetting()
    {
        var config = new TwpConfig
        {
            Plus = new PlusConfig { ControlPlane = new ControlPlaneConfig { DashboardViewerSecretEnv = "NOPE" } },
        };

        var errors = SecretResolver.Resolve(config, Env());

        Assert.AreEqual(1, errors.Count, string.Join("\n", errors));
        StringAssert.Contains(errors[0], "plus.controlPlane.dashboardViewerSecretEnv 'NOPE' is not set");
    }

    [TestMethod]
    public void OidcSecrets_AreChecked_NamedOnFailure_AndRedacted()
    {
        var keyFile = WriteSecret("cookie-keys", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\nfedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210\n");
        var config = new TwpConfig
        {
            Policies =
            [
                new Titanium.Web.Proxy.Abstractions.Routing.RoutePolicyConfig
                {
                    Name = "login", Kind = "oidc", ClientSecretEnv = "OIDC_SECRET", CookieSecretFile = keyFile,
                },
            ],
        };

        var errors = SecretResolver.Resolve(config, Env(("OIDC_SECRET", "idp-client-secret")));

        Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
        Assert.AreEqual("masked", SecretRedactor.Redact("masked"));
        StringAssert.DoesNotMatch(SecretRedactor.Redact("the secret is idp-client-secret"), IdpClientSecretPattern());
        StringAssert.DoesNotMatch(
            SecretRedactor.Redact("key 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            HexKeyPattern());
        CollectionAssert.Contains(SecretResolver.SecretFiles(config).ToArray(), keyFile);

        var broken = new TwpConfig
        {
            Policies =
            [
                new Titanium.Web.Proxy.Abstractions.Routing.RoutePolicyConfig
                {
                    Name = "login", Kind = "oidc", ClientSecretEnv = "NOT_SET", CookieSecretFile = Path.Combine(_dir, "missing"),
                },
                new Titanium.Web.Proxy.Abstractions.Routing.RoutePolicyConfig { Name = "other", Kind = "extAuth", Url = "http://auth.test/" },
            ],
        };
        var failures = SecretResolver.Resolve(broken, Env());
        Assert.IsTrue(failures.Any(e => e.Contains("policies.login.clientSecretEnv 'NOT_SET' is not set", StringComparison.Ordinal)), string.Join("\n", failures));
        Assert.IsTrue(failures.Any(e => e.Contains("policies.login.cookieSecretFile", StringComparison.Ordinal) && e.Contains("could not be read", StringComparison.Ordinal)), string.Join("\n", failures));
        Assert.AreEqual(2, failures.Count);
    }

    [TestMethod]
    public void AdminToken_FromFile_IsOnlyResolvedWhenAdminIsEnabled()
    {
        var path = WriteSecret("admin", "admin-from-file\n");
        var enabled = new TwpConfig
        {
            Server = new ServerConfig { Admin = new AdminConfig { Enabled = true, AuthTokenFile = path } },
        };
        var disabled = new TwpConfig
        {
            Server = new ServerConfig { Admin = new AdminConfig { Enabled = false, AuthTokenFile = Path.Combine(_dir, "nope") } },
        };

        Assert.AreEqual(0, SecretResolver.Resolve(enabled).Count);
        Assert.AreEqual("admin-from-file", enabled.Server!.Admin!.AuthToken);
        Assert.AreEqual(0, SecretResolver.Resolve(disabled).Count);
    }

    [TestMethod]
    public void ResolvedValues_AreRedactedFromConsoleText()
    {
        var path = WriteSecret("admin", "super-secret-token\n");
        var config = new TwpConfig
        {
            Plus = new PlusConfig
            {
                Options = new Dictionary<string, string> { ["security.basicUsersEnv"] = "BASIC" },
            },
            Server = new ServerConfig { Admin = new AdminConfig { Enabled = true, AuthTokenFile = path } },
        };

        SecretResolver.Resolve(config, Env(("BASIC", "alice:pw-alice")));

        Assert.AreEqual("token=*** user=***", SecretRedactor.Redact("token=super-secret-token user=pw-alice"));
        Assert.AreEqual("***", SecretRedactor.Redact("alice:pw-alice"));
        Assert.AreEqual("hello alice", SecretRedactor.Redact("hello alice"), "the user name alone is not a secret");
        Assert.AreEqual("nothing to hide", SecretRedactor.Redact("nothing to hide"));
    }

    [TestMethod]
    public void Redactor_IgnoresShortValues_AndMasksLongerSecretsWhole()
    {
        SecretRedactor.Register("abc");
        SecretRedactor.Register("hunter2");
        SecretRedactor.Register("hunter2-extended");

        Assert.AreEqual("abc stays", SecretRedactor.Redact("abc stays"));
        Assert.AreEqual("***", SecretRedactor.Redact("hunter2-extended"));
        Assert.AreEqual("*** and ***", SecretRedactor.Redact("hunter2 and hunter2-extended"));
    }

    [TestMethod]
    public void SecretFiles_ListsEveryReferencedFile()
    {
        var config = new TwpConfig
        {
            Plus = new PlusConfig
            {
                ControlPlane = new ControlPlaneConfig { SharedSecretFile = Path.Combine(_dir, "a") },
                Options = new Dictionary<string, string> { ["state.redisFile"] = Path.Combine(_dir, "b") },
            },
            Server = new ServerConfig { Admin = new AdminConfig { AuthTokenFile = Path.Combine(_dir, "c") } },
        };

        var files = SecretResolver.SecretFiles(config);

        CollectionAssert.AreEquivalent(
            new[] { Path.Combine(_dir, "a"), Path.Combine(_dir, "b"), Path.Combine(_dir, "c") },
            files.ToArray());
    }
}
