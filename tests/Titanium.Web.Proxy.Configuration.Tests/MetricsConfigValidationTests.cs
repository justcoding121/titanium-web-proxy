using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary><c>server.metrics</c> is off unless enabled, and an enabled endpoint must bind to a real address and port.</summary>
[TestClass]
public class MetricsConfigValidationTests
{
    private static string Errors(MetricsConfig? metrics) =>
        string.Join("\n", TwpConfigValidator.Validate(new TwpConfig { Server = new ServerConfig { Metrics = metrics } }));

    [TestMethod]
    public void AbsentOrDisabled_IsNotValidated()
    {
        Assert.AreEqual(string.Empty, Errors(null));
        Assert.AreEqual(string.Empty, Errors(new MetricsConfig { Enabled = false, Port = 0, Host = "not-an-ip" }));
    }

    [TestMethod]
    public void EnabledWithDefaults_IsAccepted()
    {
        Assert.AreEqual(string.Empty, Errors(new MetricsConfig { Enabled = true }));
        Assert.AreEqual(string.Empty, Errors(new MetricsConfig { Enabled = true, Host = "::1", Port = 9464 }));
    }

    [TestMethod]
    public void EnabledWithBadPortOrHost_IsRejected()
    {
        StringAssert.Contains(Errors(new MetricsConfig { Enabled = true, Port = 0 }), "server.metrics.port");
        StringAssert.Contains(Errors(new MetricsConfig { Enabled = true, Port = 70000 }), "server.metrics.port");
        StringAssert.Contains(Errors(new MetricsConfig { Enabled = true, Host = "metrics.local" }), "server.metrics.host");
    }

    [TestMethod]
    public void YamlKey_IsKnownToTheModel()
    {
        var unknown = UnknownConfigKeyScanner.Scan("server:\n  metrics:\n    enabled: true\n    port: 9464\n", yaml: true);
        Assert.AreEqual(0, unknown.Count);
    }
}
