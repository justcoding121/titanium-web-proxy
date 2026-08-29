using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Options;

namespace Titanium.Cli.Tests;

[TestClass]
public class ServerConfigApplierTests
{
    [TestMethod]
    public void Apply_PublicFacingProfile_SetsAdmissionAndTimeouts()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        ServerConfigApplier.Apply(proxy, new ServerConfig { Profile = "PublicFacing" });

        Assert.AreEqual(ProxyProfile.PublicFacing, proxy.Profile);
        Assert.IsTrue(proxy.BlockPrivateNetworkDestinations);
        Assert.AreEqual(10_000, proxy.MaxConcurrentClientConnections);
        Assert.IsTrue(proxy.ClientHeaderTimeoutSeconds > 0);
        Assert.IsTrue(proxy.RequestTimeoutSeconds > 0);
    }

    [TestMethod]
    public void Apply_TimeoutOverlays_AfterProfile()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        ServerConfigApplier.Apply(proxy, new ServerConfig
        {
            Profile = "PublicFacing",
            Timeouts = new TimeoutsConfig
            {
                ClientHeaderTimeoutSeconds = 5,
                RequestTimeoutSeconds = 90,
            },
        });

        Assert.AreEqual(5, proxy.ClientHeaderTimeoutSeconds);
        Assert.AreEqual(90, proxy.RequestTimeoutSeconds);
        Assert.IsTrue(proxy.BlockPrivateNetworkDestinations);
    }

    [TestMethod]
    public void Apply_EnableHttp2False()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        Assert.IsTrue(proxy.EnableHttp2);
        ServerConfigApplier.Apply(proxy, new ServerConfig { EnableHttp2 = false });
        Assert.IsFalse(proxy.EnableHttp2);
    }

    [TestMethod]
    public void TryParseSslProtocols_OrsFlags()
    {
        Assert.IsTrue(ServerConfigApplier.TryParseSslProtocols(["Tls12", "Tls13"], out var protocols));
        Assert.AreEqual(
            System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            protocols);
    }

    [TestMethod]
    public void TryParseEndPoint_IPv4()
    {
        Assert.IsTrue(ServerConfigApplier.TryParseEndPoint("127.0.0.1:53", out var ep));
        Assert.AreEqual(53, ep.Port);
        Assert.AreEqual(System.Net.IPAddress.Loopback, ep.Address);
    }
}
