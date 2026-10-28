using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary><c>server.admin</c> is off unless enabled; a non-loopback bind must be protected by a token.</summary>
[TestClass]
public class AdminConfigValidationTests
{
    private static string Errors(AdminConfig? admin) =>
        string.Join("\n", TwpConfigValidator.Validate(new TwpConfig { Server = new ServerConfig { Admin = admin } }));

    [TestMethod]
    public void AbsentOrDisabled_IsNotValidated()
    {
        Assert.AreEqual(string.Empty, Errors(null));
        Assert.AreEqual(string.Empty, Errors(new AdminConfig { Enabled = false, Host = "0.0.0.0", Port = -4 }));
    }

    [TestMethod]
    public void EnabledWithDefaults_IsAcceptedOnLoopback()
    {
        Assert.AreEqual(string.Empty, Errors(new AdminConfig { Enabled = true }));
        Assert.AreEqual(string.Empty, Errors(new AdminConfig { Enabled = true, Host = "127.0.0.1", Port = 0 }));
        Assert.AreEqual(string.Empty, Errors(new AdminConfig { Enabled = true, Host = "::1", Port = 9090, PreStopDelaySeconds = 5 }));
    }

    [TestMethod]
    public void NonLoopbackWithoutToken_IsAnError()
    {
        StringAssert.Contains(Errors(new AdminConfig { Enabled = true, Host = "0.0.0.0", Port = 9090 }), "authTokenEnv is required");
        StringAssert.Contains(Errors(new AdminConfig { Enabled = true, Host = "10.1.2.3" }), "authTokenEnv is required");
        StringAssert.Contains(Errors(new AdminConfig { Enabled = true, Host = "::" }), "authTokenEnv is required");
    }

    [TestMethod]
    public void NonLoopbackWithToken_IsAccepted()
    {
        Assert.AreEqual(
            string.Empty,
            Errors(new AdminConfig { Enabled = true, Host = "0.0.0.0", Port = 9090, AuthTokenEnv = "TITANIUM_ADMIN_TOKEN" }));
    }

    [TestMethod]
    public void BadPortDelayHostOrTokenName_IsRejected()
    {
        StringAssert.Contains(Errors(new AdminConfig { Enabled = true, Port = -1 }), "server.admin.port");
        StringAssert.Contains(Errors(new AdminConfig { Enabled = true, Port = 70000 }), "server.admin.port");
        StringAssert.Contains(Errors(new AdminConfig { Enabled = true, PreStopDelaySeconds = -1 }), "preStopDelaySeconds");
        StringAssert.Contains(Errors(new AdminConfig { Enabled = true, PreStopDelaySeconds = 100000 }), "preStopDelaySeconds");
        StringAssert.Contains(Errors(new AdminConfig { Enabled = true, Host = "admin.local" }), "server.admin.host");
        StringAssert.Contains(Errors(new AdminConfig { Enabled = true, AuthTokenEnv = "  " }), "authTokenEnv must name");
    }

    [TestMethod]
    public void YamlKeys_AreKnownToTheModel()
    {
        const string yaml = """
            server:
              admin:
                enabled: true
                host: "127.0.0.1"
                port: 0
                authTokenEnv: TITANIUM_ADMIN_TOKEN
                preStopDelaySeconds: 5
                readiness:
                  requireHealthyClusters: true
            """;
        Assert.AreEqual(0, UnknownConfigKeyScanner.Scan(yaml, yaml: true).Count);

        var admin = TwpConfigLoader.LoadYaml(yaml).Server!.Admin!;
        Assert.IsTrue(admin.Enabled);
        Assert.AreEqual("TITANIUM_ADMIN_TOKEN", admin.AuthTokenEnv);
        Assert.AreEqual(5, admin.PreStopDelaySeconds);
        Assert.IsTrue(admin.Readiness!.RequireHealthyClusters);
    }
}
