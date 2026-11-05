using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

/// <summary>Control-plane TLS, audit path, and hashed-user secrets reach Plus the way the config says.</summary>
[TestClass]
public class PlusHardeningConfigTests
{
    [TestMethod]
    public void BuildPlusOptions_CarriesTlsAndTheInsecureOptOut()
    {
        var plus = new PlusConfig
        {
            Enabled = true,
            ControlPlane = new ControlPlaneConfig
            {
                Host = "0.0.0.0",
                SharedSecret = "s3cret",
                AllowInsecureNonLoopback = true,
                Tls = new ControlPlaneTlsConfig { CertificatePath = "tls/tls.crt", KeyPath = "tls/tls.key" },
            },
        };

        var options = RunCommand.BuildPlusOptions(plus);

        Assert.AreEqual("true", options["controlPlane.allowInsecureNonLoopback"]);
        Assert.AreEqual("tls/tls.crt", options["controlPlane.tls.certificatePath"]);
        Assert.AreEqual("tls/tls.key", options["controlPlane.tls.keyPath"]);
    }

    [TestMethod]
    public void BuildPlusOptions_LeavesTlsOut_WhenNotConfigured()
    {
        var options = RunCommand.BuildPlusOptions(new PlusConfig
        {
            ControlPlane = new ControlPlaneConfig { SharedSecret = "s" },
        });

        Assert.IsFalse(options.ContainsKey("controlPlane.allowInsecureNonLoopback"));
        Assert.IsFalse(options.Keys.Any(k => k.StartsWith("controlPlane.tls.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RelativeAuditAndTlsPaths_ResolveAgainstTheConfigDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-cfg-" + Guid.NewGuid().ToString("N"));
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["audit.path"] = "logs/audit.ndjson",
            ["controlPlane.tls.certificatePath"] = "tls/tls.crt",
            ["controlPlane.tls.keyPath"] = Path.Combine(dir, "abs", "tls.key"),
        };

        RunCommand.ResolvePlusRelativePaths(options, dir);

        Assert.AreEqual(Path.GetFullPath(Path.Combine(dir, "logs", "audit.ndjson")), options["audit.path"]);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(dir, "tls", "tls.crt")), options["controlPlane.tls.certificatePath"]);
        Assert.AreEqual(Path.Combine(dir, "abs", "tls.key"), options["controlPlane.tls.keyPath"], "an absolute path is untouched");
    }

    [TestMethod]
    public void HashedBasicUsers_AndConsulToken_ResolveFromFileAndEnvironment()
    {
        SecretRedactor.Clear();
        var dir = Path.Combine(Path.GetTempPath(), "twp-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var users = Path.Combine(dir, "users");
            const string bcrypt = "$2b$05$CCCCCCCCCCCCCCCCCCCCC.E5YPO9kmyuRGyh0XouQYb4YMJKvyOeW";
            File.WriteAllText(users, $"alice:{{bcrypt}}{bcrypt}\nbob:{{pbkdf2}}10$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA==\n");
            var config = new TwpConfig
            {
                Plus = new PlusConfig
                {
                    Options = new Dictionary<string, string>
                    {
                        ["security.basicUsersFile"] = users,
                        ["discovery.consulTokenEnv"] = "CONSUL_TOKEN",
                    },
                },
            };

            var errors = SecretResolver.Resolve(
                config, name => name == "CONSUL_TOKEN" ? "consul-acl-token" : null);

            Assert.AreEqual(0, errors.Count);
            var options = config.Plus!.Options!;
            Assert.AreEqual(
                $"alice:{{bcrypt}}{bcrypt},bob:{{pbkdf2}}10$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA==",
                options["security.basicUsers"]);
            Assert.AreEqual("consul-acl-token", options["discovery.consulToken"]);
            Assert.IsFalse(options.ContainsKey("discovery.consulTokenEnv"));
        }
        finally
        {
            SecretRedactor.Clear();
            Directory.Delete(dir, recursive: true);
        }
    }
}
