using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Cli.Tests;

/// <summary><c>titanium healthcheck</c>: the probe behind the container <c>HEALTHCHECK</c>.</summary>
[TestClass]
public class HealthcheckCommandTests
{
    [TestMethod]
    public void ResolveUrl_WildcardBind_ProbesLoopback()
    {
        var config = TwpConfigLoader.LoadYaml("""
            schemaVersion: "7.1"
            server:
              admin:
                enabled: true
                host: 0.0.0.0
                port: 9901
                authTokenEnv: T
            """);

        Assert.AreEqual("http://127.0.0.1:9901/healthz", HealthcheckCommand.ResolveUrl(config, out var reason));
        Assert.IsNull(reason);
    }

    [TestMethod]
    public void ResolveUrl_IPv6Wildcard_UsesBracketedLoopback()
    {
        var config = TwpConfigLoader.LoadYaml("""
            schemaVersion: "7.1"
            server:
              admin: { enabled: true, host: "::", port: 9902, authTokenEnv: T }
            """);

        Assert.AreEqual("http://[::1]:9902/healthz", HealthcheckCommand.ResolveUrl(config, out _));
    }

    [TestMethod]
    public void ResolveUrl_SpecificHost_IsKept()
    {
        var config = TwpConfigLoader.LoadYaml("""
            schemaVersion: "7.1"
            server:
              admin: { enabled: true, host: 10.1.2.3, port: 9901, authTokenEnv: T }
            """);

        Assert.AreEqual("http://10.1.2.3:9901/healthz", HealthcheckCommand.ResolveUrl(config, out _));
    }

    [TestMethod]
    public void ResolveUrl_AdminOffOrPortZero_HasNothingToProbe()
    {
        var off = TwpConfigLoader.LoadYaml("schemaVersion: \"7.1\"\n");
        Assert.IsNull(HealthcheckCommand.ResolveUrl(off, out var offReason));
        StringAssert.Contains(offReason, "not enabled");

        var ephemeral = TwpConfigLoader.LoadYaml("""
            schemaVersion: "7.1"
            server:
              admin: { enabled: true, port: 0 }
            """);
        Assert.IsNull(HealthcheckCommand.ResolveUrl(ephemeral, out var portReason));
        StringAssert.Contains(portReason, "port is 0");
    }

    [TestMethod]
    public async Task Execute_Url_IsZeroWhenHealthy_AndOneWhenUnreachable()
    {
        var readiness = new ReadinessState();
        var exposition = new MetricsExposition();
        using var server = new AdminServer(
            IPAddress.Loopback, 0, readiness, exposition,
            () => new AdminVersionInfo("7.0.27", "abc", ".NET", "community", "h"), "t");
        server.Start();

        Assert.AreEqual(0, await HealthcheckCommand.ExecuteAsync(
            ["healthcheck", "--url", $"http://127.0.0.1:{server.Port}/healthz"]));
        // /readyz is 503 until the listeners are bound.
        Assert.AreEqual(1, await HealthcheckCommand.ExecuteAsync(
            ["healthcheck", "--url", $"http://127.0.0.1:{server.Port}/readyz", "--timeout", "2"]));

        var closedPort = server.Port;
        server.Dispose();
        Assert.AreEqual(1, await HealthcheckCommand.ExecuteAsync(
            ["healthcheck", "--url", $"http://127.0.0.1:{closedPort}/healthz", "--timeout", "2"]));
    }

    [TestMethod]
    public async Task Execute_ConfigWithAdminOff_PassesWithoutProbing()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-hc-" + Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(path, "schemaVersion: \"7.1\"\n");
        try
        {
            Assert.AreEqual(0, await HealthcheckCommand.ExecuteAsync(["healthcheck", "--config", path]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Execute_WithoutTarget_OrMissingConfig_IsAnError()
    {
        Assert.AreEqual(1, await HealthcheckCommand.ExecuteAsync(["healthcheck"]));
        Assert.AreEqual(1, await HealthcheckCommand.ExecuteAsync(
            ["healthcheck", "-c", Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".yaml")]));
    }
}
