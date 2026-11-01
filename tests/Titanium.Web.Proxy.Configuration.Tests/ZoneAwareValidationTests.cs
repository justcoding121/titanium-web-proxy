using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class ZoneAwareValidationTests
{
    [TestMethod]
    public void ZoneAware_RequiresLocalZoneAndZonedDestinations()
    {
        var config = new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c",
                    ZoneAware = new ZoneAwareConfig(),
                    Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80 }],
                },
            ],
        };
        var errors = TwpConfigValidator.Validate(config);
        StringAssert.Contains(string.Join("; ", errors), "server.localZone");
        StringAssert.Contains(string.Join("; ", errors), "at least one destination with a zone");
    }

    [TestMethod]
    public void ZoneAware_ValidWhenConfigured()
    {
        var config = new TwpConfig
        {
            Server = new ServerConfig { LocalZone = "zone-a" },
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c",
                    ZoneAware = new ZoneAwareConfig { MinLocalHealthyPercent = 70 },
                    Destinations =
                    [
                        new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80, Zone = "zone-a" },
                    ],
                },
            ],
        };
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }
}
