using System.Collections.Generic;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
[DoNotParallelize]
public class GeoPolicyTests
{
    private static readonly TransparentProxyEndPoint EndPoint =
        new(IPAddress.Loopback, 0, false) { ForwardHost = "127.0.0.1", ForwardPort = 80 };

    [TestInitialize]
    public void Reset() => RoutePolicies.ResetForTests();

    [TestCleanup]
    public void Restore() => RoutePolicies.ResetForTests();

    private sealed class MapGeoIpResolver(Dictionary<IPAddress, string> map) : IGeoIpResolver
    {
        public bool TryGetCountry(IPAddress address, out string countryIsoCode)
        {
            if (map.TryGetValue(address, out var code))
            {
                countryIsoCode = code;
                return true;
            }

            countryIsoCode = string.Empty;
            return false;
        }
    }

    private static ReverseProxyOptions Options(IGeoIpResolver? resolver, params RoutePolicyConfig[] policies) => new()
    {
        GeoIpResolver = resolver,
        Policies = policies,
        Routes =
        [
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c",
                Policies = [policies[0].Name],
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        ],
    };

    private static Request Req() => new() { Method = "GET", RequestUriString8 = (ByteString)"/", Host = "app" };

    private static bool Denied(ReverseProxyOptions options, string client, out int status) =>
        RoutePolicies.TryHandleLite(
            options,
            EndPoint,
            Req(),
            new IPEndPoint(IPAddress.Parse(client), 1000),
            out status,
            out _,
            out _);

    [TestMethod]
    public void DenyList_BlocksMatchingCountry()
    {
        var resolver = new MapGeoIpResolver(new Dictionary<IPAddress, string> { [IPAddress.Parse("10.1.2.3")] = "XX" });
        var options = Options(resolver, new RoutePolicyConfig { Name = "geo", Kind = "geo", Deny = "XX" });
        Assert.IsTrue(Denied(options, "10.1.2.3", out var status));
        Assert.AreEqual(403, status);
        Assert.IsFalse(Denied(options, "10.1.2.4", out _));
    }

    [TestMethod]
    public void AllowList_AdmitOnlyListedCountries_UnknownFailsClosed()
    {
        var resolver = new MapGeoIpResolver(new Dictionary<IPAddress, string>
        {
            [IPAddress.Parse("10.0.0.1")] = "US",
            [IPAddress.Parse("10.0.0.2")] = "CA",
        });
        var options = Options(resolver, new RoutePolicyConfig { Name = "geo", Kind = "geo", Allow = "us, CA" });
        Assert.IsFalse(Denied(options, "10.0.0.1", out _));
        Assert.IsFalse(Denied(options, "10.0.0.2", out _));
        Assert.IsTrue(Denied(options, "10.0.0.3", out var status));
        Assert.AreEqual(403, status);
    }

    [TestMethod]
    public void DenyOnly_PassesUnknownCountry()
    {
        var resolver = new MapGeoIpResolver(new Dictionary<IPAddress, string> { [IPAddress.Parse("10.0.0.5")] = "XX" });
        var options = Options(resolver, new RoutePolicyConfig { Name = "geo", Kind = "geo", Deny = "XX" });
        Assert.IsTrue(Denied(options, "10.0.0.5", out _));
        Assert.IsFalse(Denied(options, "10.0.0.9", out _));
    }

    [TestMethod]
    public void MissingResolver_FailsClosedForAllow_OpenForDenyOnly()
    {
        var allow = Options(null, new RoutePolicyConfig { Name = "geo", Kind = "geo", Allow = "US" });
        Assert.IsTrue(Denied(allow, "10.0.0.1", out _));
        var deny = Options(null, new RoutePolicyConfig { Name = "geo", Kind = "geo", Deny = "XX" });
        Assert.IsFalse(Denied(deny, "10.0.0.1", out _));
    }

    [TestMethod]
    public void WithoutClientAddress_MatchesIpAllowDenySemantics()
    {
        var allow = Options(new MapGeoIpResolver([]), new RoutePolicyConfig { Name = "geo", Kind = "geo", Allow = "US" });
        Assert.IsTrue(RoutePolicies.TryHandleLite(allow, EndPoint, Req(), null, out var status, out _, out _));
        Assert.AreEqual(403, status);
        var deny = Options(new MapGeoIpResolver([]), new RoutePolicyConfig { Name = "geo", Kind = "geo", Deny = "US" });
        Assert.IsFalse(RoutePolicies.TryHandleLite(deny, EndPoint, Req(), null, out _, out _, out _));
    }

}
