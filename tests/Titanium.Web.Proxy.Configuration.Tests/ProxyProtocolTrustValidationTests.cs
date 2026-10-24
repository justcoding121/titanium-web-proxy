using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class ProxyProtocolTrustValidationTests
{
    [TestMethod]
    public void Validate_RejectsMalformedCidr()
    {
        var config = new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig
                {
                    Port = 8080,
                    AcceptProxyProtocol = true,
                    TrustedProxyProtocolSources = ["10.0.0.0/99", "ok"],
                },
            ],
        };

        var errors = TwpConfigValidator.Validate(config);
        StringAssert.Contains(string.Join("\n", errors), "10.0.0.0/99");
        StringAssert.Contains(string.Join("\n", errors), "ok");
    }

    [TestMethod]
    public void CollectWarnings_FlagsOpenProxyProtocol()
    {
        var config = new TwpConfig
        {
            Listeners = [new ListenerConfig { Port = 8080, AcceptProxyProtocol = true }],
        };
        var warnings = string.Join("\n", TwpConfigValidator.CollectWarnings(config));
        StringAssert.Contains(warnings, "trustedProxyProtocolSources");

        config.Listeners[0].TrustedProxyProtocolSources = ["127.0.0.1/32"];
        Assert.AreEqual(0, TwpConfigValidator.CollectWarnings(config).Count);
    }

    [TestMethod]
    public void Validate_RejectsUnixSocketsOnWindows_AndUnknownTemplateConstraints()
    {
        var config = new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c",
                    Destinations = [new DestinationConfig { Id = "d", Address = "unix:/tmp/app.sock", Port = 0 }],
                },
            ],
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/users/{id:nope}", PathKind = PathMatchKind.Template },
                },
            ],
        };
        var errors = string.Join("\n", TwpConfigValidator.Validate(config));
        StringAssert.Contains(errors, "unknown template constraint");
        if (OperatingSystem.IsWindows())
        {
            StringAssert.Contains(errors, "unix socket");
        }
    }
}
