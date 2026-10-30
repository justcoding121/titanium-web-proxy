using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>A secret is a literal, an <c>*Env</c> name, or a <c>*File</c> path: never more than one, and never interpolated.</summary>
[TestClass]
public class SecretReferenceValidationTests
{
    private static string Errors(PlusConfig plus) =>
        string.Join("\n", TwpConfigValidator.Validate(new TwpConfig { Plus = plus }));

    private static string AdminErrors(AdminConfig admin) =>
        string.Join("\n", TwpConfigValidator.Validate(new TwpConfig { Server = new ServerConfig { Admin = admin } }));

    [TestMethod]
    public void ControlPlane_AnySingleForm_IsAccepted()
    {
        foreach (var controlPlane in new[]
                 {
                     new ControlPlaneConfig { SharedSecret = "literal" },
                     new ControlPlaneConfig { SharedSecretEnv = "TITANIUM_SECRET" },
                     new ControlPlaneConfig { SharedSecretFile = "/etc/titanium/secret/shared" },
                     new ControlPlaneConfig(),
                 })
        {
            Assert.AreEqual(string.Empty, Errors(new PlusConfig { Enabled = true, ControlPlane = controlPlane }));
        }
    }

    [TestMethod]
    public void ControlPlane_MoreThanOneForm_IsRejected()
    {
        var both = Errors(new PlusConfig
        {
            ControlPlane = new ControlPlaneConfig { SharedSecret = "x", SharedSecretEnv = "ENV" },
        });
        StringAssert.Contains(both, "Set exactly one of plus.controlPlane.sharedSecret, plus.controlPlane.sharedSecretEnv");

        var all = Errors(new PlusConfig
        {
            ControlPlane = new ControlPlaneConfig { SharedSecret = "x", SharedSecretEnv = "ENV", SharedSecretFile = "/f" },
        });
        StringAssert.Contains(all, "sharedSecretFile");
    }

    [TestMethod]
    public void DashboardViewerSecret_AnySingleForm_IsAccepted()
    {
        foreach (var controlPlane in new[]
                 {
                     new ControlPlaneConfig { DashboardViewerSecret = "literal" },
                     new ControlPlaneConfig { DashboardViewerSecretEnv = "TITANIUM_VIEWER" },
                     new ControlPlaneConfig { DashboardViewerSecretFile = "/etc/titanium/secret/viewer" },
                     new ControlPlaneConfig { SharedSecret = "x", DashboardViewerSecret = "y" },
                 })
        {
            Assert.AreEqual(string.Empty, Errors(new PlusConfig { Enabled = true, ControlPlane = controlPlane }));
        }
    }

    [TestMethod]
    public void DashboardViewerSecret_MoreThanOneForm_IsRejected()
    {
        var errors = Errors(new PlusConfig
        {
            ControlPlane = new ControlPlaneConfig { DashboardViewerSecret = "x", DashboardViewerSecretFile = "/f" },
        });

        StringAssert.Contains(errors, "Set exactly one of plus.controlPlane.dashboardViewerSecret");
        StringAssert.Contains(errors, "dashboardViewerSecretFile");
    }

    [TestMethod]
    public void DashboardViewerSecret_YamlAndSchemaKeys_AreKnown()
    {
        const string yaml = """
            plus:
              enabled: true
              controlPlane:
                sharedSecretFile: /etc/titanium/secret/shared
                dashboardViewerSecretEnv: TITANIUM_VIEWER
            """;
        Assert.AreEqual(0, UnknownConfigKeyScanner.Scan(yaml, yaml: true).Count);
        var config = TwpConfigLoader.LoadYaml(yaml);
        Assert.AreEqual("TITANIUM_VIEWER", config.Plus!.ControlPlane!.DashboardViewerSecretEnv);
        Assert.AreEqual(string.Empty, string.Join("\n", TwpConfigValidator.Validate(config)));

        // The checked-in schema is compared with the model by EscapeHatchConfigTests.SchemaFile_MatchesTheModel.
        var schema = TwpConfigSchema.Generate();
        foreach (var key in new[] { "dashboardViewerSecret", "dashboardViewerSecretEnv", "dashboardViewerSecretFile" })
        {
            StringAssert.Contains(schema, $"\"{key}\"");
        }
    }

    [TestMethod]
    public void EmptyReferenceNames_AreRejected()
    {
        StringAssert.Contains(
            Errors(new PlusConfig { ControlPlane = new ControlPlaneConfig { SharedSecretEnv = " " } }),
            "sharedSecretEnv must name an environment variable");
        StringAssert.Contains(
            Errors(new PlusConfig { ControlPlane = new ControlPlaneConfig { SharedSecretFile = "" } }),
            "sharedSecretFile must name a file");
    }

    [TestMethod]
    public void PlusOptions_EachSensitiveKeyAcceptsOneForm()
    {
        foreach (var key in SecretReferenceKeys.PlusOptionKeys)
        {
            foreach (var form in new[] { key, key + "Env", key + "File" })
            {
                var plus = new PlusConfig { Options = new Dictionary<string, string> { [form] = "value" } };
                Assert.AreEqual(string.Empty, Errors(plus), form);
            }
        }
    }

    [TestMethod]
    public void PlusOptions_MoreThanOneForm_IsRejectedPerKey()
    {
        foreach (var key in SecretReferenceKeys.PlusOptionKeys)
        {
            var plus = new PlusConfig
            {
                Options = new Dictionary<string, string> { [key] = "a", [key + "File"] = "/secret" },
            };
            var errors = Errors(plus);
            StringAssert.Contains(errors, $"plus.options.{key}", key);
            StringAssert.Contains(errors, "Set exactly one of", key);
        }
    }

    [TestMethod]
    public void PlusOptions_SiblingNamesMatchIgnoringCase()
    {
        var plus = new PlusConfig
        {
            Options = new Dictionary<string, string> { ["Security.ApiKeys"] = "a", ["security.apikeysenv"] = "ENV" },
        };

        StringAssert.Contains(Errors(plus), "plus.options.security.apiKeys");
    }

    [TestMethod]
    public void PlusOptions_OtherKeysAreNotTreatedAsSecrets()
    {
        var plus = new PlusConfig
        {
            Options = new Dictionary<string, string> { ["cache.enable"] = "true", ["cache.enableEnv"] = "x" },
        };

        Assert.AreEqual(string.Empty, Errors(plus));
    }

    [TestMethod]
    public void AdminToken_AtMostOneForm()
    {
        Assert.AreEqual(string.Empty, AdminErrors(new AdminConfig { Enabled = true, AuthTokenFile = "/run/secrets/token" }));
        StringAssert.Contains(
            AdminErrors(new AdminConfig { Enabled = true, AuthToken = "x", AuthTokenFile = "/run/secrets/token" }),
            "Set exactly one of server.admin.authToken, server.admin.authTokenFile");
    }

    [TestMethod]
    public void AdminToken_FileOrLiteralSatisfiesTheNonLoopbackRule()
    {
        Assert.AreEqual(string.Empty, AdminErrors(new AdminConfig { Enabled = true, Host = "0.0.0.0", AuthTokenFile = "/t" }));
        Assert.AreEqual(string.Empty, AdminErrors(new AdminConfig { Enabled = true, Host = "0.0.0.0", AuthToken = "literal" }));
        StringAssert.Contains(AdminErrors(new AdminConfig { Enabled = true, Host = "0.0.0.0" }), "is required");
    }

    [TestMethod]
    public void YamlKeys_AreKnownToTheModel()
    {
        const string yaml = """
            plus:
              enabled: true
              controlPlane:
                host: 127.0.0.1
                port: 9080
                sharedSecretFile: /etc/titanium/secret/shared
              options:
                security.apiKeysEnv: TITANIUM_API_KEYS
                state.redisFile: /etc/titanium/secret/redis
            server:
              admin:
                enabled: true
                authTokenFile: /etc/titanium/secret/admin
            """;
        Assert.AreEqual(0, UnknownConfigKeyScanner.Scan(yaml, yaml: true).Count);
        var config = TwpConfigLoader.LoadYaml(yaml);
        Assert.AreEqual("/etc/titanium/secret/shared", config.Plus!.ControlPlane!.SharedSecretFile);
        Assert.AreEqual("TITANIUM_API_KEYS", config.Plus.Options!["security.apiKeysEnv"]);
        Assert.AreEqual("/etc/titanium/secret/admin", config.Server!.Admin!.AuthTokenFile);
        Assert.AreEqual(string.Empty, string.Join("\n", TwpConfigValidator.Validate(config)));
    }
}
