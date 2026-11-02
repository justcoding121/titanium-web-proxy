using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class GeoPolicyValidationTests
{
    private static string Errors(TwpConfig config) => string.Join("; ", TwpConfigValidator.Validate(config));

    private static TwpConfig BaseConfig(RoutePolicyConfig policy) => new()
    {
        Clusters = [new ClusterConfig { Id = "c", Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80 }] }],
        Policies = [policy],
        Routes = [new RouteConfig { Id = "r", ClusterId = "c", Match = new RouteMatch { Path = "/" }, Policies = [policy.Name] }],
    };

    [TestMethod]
    public void ValidGeoPolicy_WithDatabasePath_Passes()
    {
        var policy = new RoutePolicyConfig { Name = "geo", Kind = "geo", Deny = "XX, yy" };
        var config = BaseConfig(policy);
        config.Server = new ServerConfig { GeoIpDatabasePath = "/data/GeoLite2-Country.mmdb" };
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count, Errors(config));
    }

    [TestMethod]
    public void GeoPolicy_RequiresAllowOrDeny()
    {
        var config = BaseConfig(new RoutePolicyConfig { Name = "geo", Kind = "geo" });
        config.Server = new ServerConfig { GeoIpDatabasePath = "/data/country.mmdb" };
        StringAssert.Contains(Errors(config), "(geo) needs allow or deny");
    }

    [TestMethod]
    public void GeoPolicy_RequiresServerGeoIpDatabasePath()
    {
        var config = BaseConfig(new RoutePolicyConfig { Name = "geo", Kind = "geo", Deny = "XX" });
        StringAssert.Contains(Errors(config), "server.geoIpDatabasePath");
    }

    [TestMethod]
    public void GeoPolicy_RejectsInvalidTokensAndTooManyCodes()
    {
        var config = BaseConfig(new RoutePolicyConfig
        {
            Name = "geo",
            Kind = "geo",
            Allow = "USA, x",
        });
        config.Server = new ServerConfig { GeoIpDatabasePath = "/data/country.mmdb" };
        var errors = Errors(config);
        StringAssert.Contains(errors, "'USA'");
        StringAssert.Contains(errors, "'x'");

        var many = string.Join(',', Enumerable.Range(0, 301).Select(i =>
            $"{(char)('A' + (i % 26))}{(char)('A' + ((i / 26) % 26))}"));
        var tooMany = BaseConfig(new RoutePolicyConfig { Name = "geo", Kind = "geo", Allow = many });
        tooMany.Server = new ServerConfig { GeoIpDatabasePath = "/data/country.mmdb" };
        StringAssert.Contains(Errors(tooMany), "more than 300");
    }
}
