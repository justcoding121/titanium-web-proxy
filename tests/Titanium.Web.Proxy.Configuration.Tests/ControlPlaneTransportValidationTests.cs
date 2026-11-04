using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>A control plane reachable from the network needs TLS, or an explicit opt-out.</summary>
[TestClass]
public class ControlPlaneTransportValidationTests
{
    private static string Errors(PlusConfig plus) =>
        string.Join("\n", TwpConfigValidator.Validate(new TwpConfig { Plus = plus }));

    private static PlusConfig Plus(ControlPlaneConfig controlPlane, bool enabled = true) =>
        new() { Enabled = enabled, ControlPlane = controlPlane };

    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("localhost")]
    [DataRow("::1")]
    [DataRow("[::1]")]
    [DataRow("127.0.0.2")]
    public void Loopback_NeedsNoTls(string host)
    {
        Assert.AreEqual(string.Empty, Errors(Plus(new ControlPlaneConfig { Host = host })));
    }

    [TestMethod]
    [DataRow("0.0.0.0")]
    [DataRow("::")]
    [DataRow("10.1.2.3")]
    [DataRow("proxy.example.com")]
    public void NonLoopback_WithoutTlsOrOptOut_IsRejected(string host)
    {
        var errors = Errors(Plus(new ControlPlaneConfig { Host = host }));

        StringAssert.Contains(errors, "is not loopback");
        StringAssert.Contains(errors, "plus.controlPlane.tls");
        StringAssert.Contains(errors, "allowInsecureNonLoopback");
    }

    [TestMethod]
    public void NonLoopback_WithTls_IsAccepted()
    {
        var controlPlane = new ControlPlaneConfig
        {
            Host = "0.0.0.0",
            Tls = new ControlPlaneTlsConfig { CertificatePath = "/tls/tls.crt", KeyPath = "/tls/tls.key" },
        };

        Assert.AreEqual(string.Empty, Errors(Plus(controlPlane)));
        Assert.AreEqual(0, TwpConfigValidator.CollectWarnings(new TwpConfig { Plus = Plus(controlPlane) }).Count);
    }

    [TestMethod]
    public void NonLoopback_WithExplicitOptOut_IsAccepted_AndWarns()
    {
        var config = new TwpConfig
        {
            Plus = Plus(new ControlPlaneConfig { Host = "0.0.0.0", AllowInsecureNonLoopback = true }),
        };

        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
        var warnings = TwpConfigValidator.CollectWarnings(config);
        Assert.IsTrue(
            warnings.Any(w => w.Contains("plain HTTP", StringComparison.Ordinal) && w.Contains("0.0.0.0", StringComparison.Ordinal)),
            string.Join("; ", warnings));
    }

    [TestMethod]
    public void OptOut_OnLoopback_DoesNotWarn()
    {
        var config = new TwpConfig { Plus = Plus(new ControlPlaneConfig { AllowInsecureNonLoopback = true }) };

        Assert.AreEqual(0, TwpConfigValidator.CollectWarnings(config).Count);
    }

    [TestMethod]
    public void PlusDisabled_IgnoresTheTransportCheck()
    {
        Assert.AreEqual(string.Empty, Errors(Plus(new ControlPlaneConfig { Host = "0.0.0.0" }, enabled: false)));
    }

    [TestMethod]
    [DataRow("/c.pem", null)]
    [DataRow(null, "/k.pem")]
    [DataRow("", "/k.pem")]
    [DataRow("/c.pem", " ")]
    public void Tls_NeedsBothPaths(string? cert, string? key)
    {
        var errors = Errors(Plus(new ControlPlaneConfig
        {
            Tls = new ControlPlaneTlsConfig { CertificatePath = cert, KeyPath = key },
        }));

        StringAssert.Contains(errors, "needs both certificatePath and keyPath");
    }

    [TestMethod]
    public void ConsulToken_AcceptsASecretReference_ButNotTwoForms()
    {
        var plus = new PlusConfig
        {
            Options = new Dictionary<string, string>
            {
                ["discovery.consulTokenFile"] = "/run/secrets/consul",
                ["discovery.consulTokenEnv"] = "CONSUL_TOKEN",
            },
        };

        StringAssert.Contains(Errors(plus), "plus.options.discovery.consulToken");
        plus.Options.Remove("discovery.consulTokenEnv");
        Assert.AreEqual(string.Empty, Errors(plus));
    }

    [TestMethod]
    public void YamlRoundTrip_ReadsTlsAndOptOut()
    {
        var config = TwpConfigLoader.LoadYaml("""
            plus:
              enabled: true
              controlPlane:
                host: 0.0.0.0
                sharedSecretEnv: TITANIUM_CONTROL_SECRET
                allowInsecureNonLoopback: false
                tls:
                  certificatePath: /tls/tls.crt
                  keyPath: /tls/tls.key
            """);

        var controlPlane = config.Plus!.ControlPlane!;
        Assert.AreEqual("/tls/tls.crt", controlPlane.Tls!.CertificatePath);
        Assert.AreEqual("/tls/tls.key", controlPlane.Tls.KeyPath);
        Assert.IsFalse(controlPlane.AllowInsecureNonLoopback);
        Assert.AreEqual(string.Empty, Errors(config.Plus));
    }
}
