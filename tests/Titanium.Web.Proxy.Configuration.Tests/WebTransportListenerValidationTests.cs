using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class WebTransportListenerValidationTests
{
    [TestMethod]
    public void Properties_RoundTrip()
    {
        var config = new WebTransportListenerConfig
        {
            RequireAuth = false,
            AllowHttp11Upgrade = true,
            Credentials = [new MasqueCredentialConfig { Username = "ada", PasswordEnv = "WT_PASS" }],
            OriginHost = "origin.test",
            OriginPort = 8443,
            OriginHttps = false,
            MaxSessions = 4,
            MaxSessionsPerClient = 2,
        };

        Assert.IsFalse(config.RequireAuth);
        Assert.IsTrue(config.AllowHttp11Upgrade);
        Assert.AreEqual("ada", config.Credentials![0].Username);
        Assert.AreEqual("origin.test", config.OriginHost);
        Assert.AreEqual(8443, config.OriginPort);
        Assert.IsFalse(config.OriginHttps);
        Assert.AreEqual(4, config.MaxSessions);
        Assert.AreEqual(2, config.MaxSessionsPerClient);
    }

    [TestMethod]
    public void RejectsSettingsOnTheWrongListener_AndIncompleteTerminateListeners()
    {
        var wrongType = Errors(new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = 4433,
            WebTransport = new WebTransportListenerConfig { OriginHost = "origin.test" },
        });
        StringAssert.Contains(wrongType, "webtransport settings require type: webtransport");

        var openUdp = Errors(new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = 4433,
            AcceptConnectUdp = true,
        });
        StringAssert.Contains(openUdp, "acceptConnectUdp requires credentials");

        StringAssert.Contains(Errors(Terminate(webTransport: null)), "requires credentials or requireAuth: false");
        StringAssert.Contains(Errors(Terminate(originHost: "  ")), "requires originHost");
        StringAssert.Contains(Errors(Terminate(originPort: 0)), "originPort is out of range");
        StringAssert.Contains(Errors(Terminate(originPort: 70000)), "originPort is out of range");
        StringAssert.Contains(Errors(Terminate(maxSessions: 0)), "maxSessions must be positive");
        StringAssert.Contains(Errors(Terminate(maxSessionsPerClient: 0)), "maxSessionsPerClient must be positive");
    }

    [TestMethod]
    public void WarnsWhenAuthenticationIsOffOnAPublicAddress()
    {
        var config = new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig
                {
                    Host = "10.1.2.3",
                    Port = 4433,
                    Type = "webtransport",
                    WebTransport = new WebTransportListenerConfig
                    {
                        RequireAuth = false,
                        OriginHost = "origin.test",
                    },
                },
            ],
        };

        var warnings = string.Join("\n", TwpConfigValidator.CollectWarnings(config));
        StringAssert.Contains(warnings, "webtransport authentication is off on a non-loopback address");
    }

    private static string Errors(ListenerConfig listener) =>
        string.Join("\n", TwpConfigValidator.Validate(new TwpConfig { Listeners = [listener] }));

    private static ListenerConfig Terminate(
        WebTransportListenerConfig? webTransport = null,
        string? originHost = "origin.test",
        int? originPort = null,
        int? maxSessions = null,
        int? maxSessionsPerClient = null) =>
        new()
        {
            Host = "127.0.0.1",
            Port = 4433,
            Type = "webtransport",
            WebTransport = webTransport ?? new WebTransportListenerConfig
            {
                OriginHost = originHost,
                OriginPort = originPort,
                MaxSessions = maxSessions,
                MaxSessionsPerClient = maxSessionsPerClient,
            },
        };
}
