using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

/// <summary>
///     Features that used to start without Plus must fail <c>titanium test</c> unless
///     <c>plus.enabled</c> is set (mirrors oidc / waf gating).
/// </summary>
[TestClass]
public class PlusGatingValidationTests
{
    private static string Errors(TwpConfig config) => string.Join("; ", TwpConfigValidator.Validate(config));

    private static ClusterConfig Cluster(string id, bool activeHealth = false) => new()
    {
        Id = id,
        Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80 }],
        HealthCheck = activeHealth
            ? new HealthCheckConfig { Active = new ActiveHealthConfig { Enabled = true } }
            : null,
    };

    [TestMethod]
    public void CacheEnable_WithoutPlus_IsRejected()
    {
        var config = new TwpConfig
        {
            Plus = new PlusConfig
            {
                Enabled = false,
                Options = new Dictionary<string, string> { ["cache.enable"] = "true" },
            },
            Clusters = [Cluster("c")],
        };

        StringAssert.Contains(Errors(config), "cache.enable needs plus.enabled: true");
    }

    [TestMethod]
    public void CacheEnable_WithPlus_Passes()
    {
        var config = new TwpConfig
        {
            Plus = new PlusConfig
            {
                Enabled = true,
                Options = new Dictionary<string, string> { ["cache.enable"] = "true" },
            },
            Clusters = [Cluster("c")],
        };

        Assert.AreEqual(string.Empty, Errors(config));
    }

    [TestMethod]
    public void ActiveHealth_WithoutPlus_IsRejected()
    {
        var config = new TwpConfig { Clusters = [Cluster("c", activeHealth: true)] };
        StringAssert.Contains(Errors(config), "healthCheck.active needs plus.enabled: true");
    }

    [TestMethod]
    public void ActiveHealth_WithPlus_Passes()
    {
        var config = new TwpConfig
        {
            Plus = new PlusConfig { Enabled = true },
            Clusters = [Cluster("c", activeHealth: true)],
        };

        Assert.AreEqual(string.Empty, Errors(config));
    }

    [TestMethod]
    public void GeoIpDatabasePathAlone_WithoutPlus_IsRejected()
    {
        var config = new TwpConfig
        {
            Clusters = [Cluster("c")],
            Server = new ServerConfig { GeoIpDatabasePath = "/data/country.mmdb" },
        };

        StringAssert.Contains(Errors(config), "server.geoIpDatabasePath needs plus.enabled: true");
    }
}
