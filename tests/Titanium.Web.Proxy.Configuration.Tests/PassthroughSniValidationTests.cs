using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class PassthroughSniValidationTests
{
    private static readonly string[] TcpApiWebClusterIds = ["tcp-api", "tcp-web"];

    private static ClusterConfig Cluster(string id) => new()
    {
        Id = id,
        Destinations = [new DestinationConfig { Id = id + "-d", Address = "127.0.0.1", Port = 9 }],
    };

    private static TwpConfig Config(ListenerConfig listener, params string[] clusters) => new()
    {
        Listeners = [listener],
        Clusters = clusters.Select(Cluster).ToList(),
    };

    private static ListenerConfig Listener(params SniRouteConfig[] routes) => new()
    {
        Port = 8443,
        Type = "transparent",
        Passthrough = true,
        SniRoutes = routes,
    };

    private static SniRouteConfig Route(string name, string cluster) => new() { ServerName = name, ClusterId = cluster };

    private static string Errors(TwpConfig config) => string.Join("\n", TwpConfigValidator.Validate(config));

    [TestMethod]
    public void WellFormedSniListener_IsAccepted()
    {
        var listener = Listener(Route("api.example.com", "a"), Route("*.example.com", "b"));
        listener.SniDefaultClusterId = "b";
        listener.SniReadTimeoutSeconds = 5;
        listener.IdleTimeoutSeconds = 0;
        listener.SendProxyProtocol = "v2";

        Assert.AreEqual(string.Empty, Errors(Config(listener, "a", "b")));
    }

    [TestMethod]
    public void DocumentedYaml_LoadsAndValidates()
    {
        const string yaml = """
            listeners:
              - port: 443
                passthrough: true
                decryptSsl: false
                sniRoutes:
                  - serverName: "api.example.com"
                    clusterId: tcp-api
                  - serverName: "*.example.com"
                    clusterId: tcp-web
                sniDefaultClusterId: tcp-web
                sniReadTimeoutSeconds: 5
                idleTimeoutSeconds: 0
                sendProxyProtocol: v2
            """;

        var config = TwpConfigLoader.LoadYaml(yaml);
        config.Clusters = TcpApiWebClusterIds.Select(Cluster).ToList();

        var listener = config.Listeners.Single();
        Assert.AreEqual(2, listener.SniRoutes.Count);
        Assert.AreEqual("*.example.com", listener.SniRoutes[1].ServerName);
        Assert.AreEqual("tcp-web", listener.SniDefaultClusterId);
        Assert.AreEqual("v2", listener.SendProxyProtocol);
        Assert.AreEqual(string.Empty, Errors(config));
    }

    [TestMethod]
    public void ListenerWithoutAType_IsTreatedAsTransparentWhenItUsesSniRoutes()
    {
        var listener = Listener(Route("api.example.com", "a"));
        listener.Type = null;

        Assert.AreEqual(string.Empty, Errors(Config(listener, "a")));
    }

    [TestMethod]
    public void UnknownClusterIds_AreRejected()
    {
        var listener = Listener(Route("api.example.com", "missing"));
        listener.SniDefaultClusterId = "alsoMissing";

        var errors = Errors(Config(listener, "a"));

        StringAssert.Contains(errors, "sniRoutes[0].clusterId 'missing'");
        StringAssert.Contains(errors, "sniDefaultClusterId 'alsoMissing'");
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("*")]
    [DataRow("*.")]
    [DataRow("**.example.com")]
    [DataRow("*example.com")]
    [DataRow("a.*.example.com")]
    [DataRow("*.*.example.com")]
    [DataRow("example.com*")]
    [DataRow(".example.com")]
    [DataRow("example..com")]
    [DataRow("exa mple.com")]
    [DataRow("example.com/path")]
    public void BadServerNamePatterns_AreRejected(string pattern)
    {
        var errors = Errors(Config(Listener(Route(pattern, "a")), "a"));

        StringAssert.Contains(errors, "sniRoutes[0].serverName");
    }

    [DataTestMethod]
    [DataRow("example.com")]
    [DataRow("*.example.com")]
    [DataRow("a-b.example.com")]
    [DataRow("localhost")]
    [DataRow("*.local")]
    public void GoodServerNamePatterns_AreAccepted(string pattern)
    {
        Assert.AreEqual(string.Empty, Errors(Config(Listener(Route(pattern, "a")), "a")));
    }

    [TestMethod]
    public void DuplicateServerNames_AreRejectedIgnoringCase()
    {
        var errors = Errors(Config(Listener(Route("a.example.com", "a"), Route("A.EXAMPLE.com", "a")), "a"));

        StringAssert.Contains(errors, "listed twice");
    }

    [TestMethod]
    public void SniRoutes_RequirePassthroughAndNoDecryption()
    {
        var notPassthrough = Listener(Route("a.example.com", "a"));
        notPassthrough.Passthrough = false;
        StringAssert.Contains(Errors(Config(notPassthrough, "a")), "require passthrough: true");

        var decrypting = Listener(Route("a.example.com", "a"));
        decrypting.DecryptSsl = true;
        StringAssert.Contains(Errors(Config(decrypting, "a")), "decryptSsl: false");

        var explicitType = Listener(Route("a.example.com", "a"));
        explicitType.Type = "explicit";
        StringAssert.Contains(Errors(Config(explicitType, "a")), "only to transparent listeners");
    }

    [TestMethod]
    public void DefaultClusterAlone_NeedsPassthroughToo()
    {
        var listener = new ListenerConfig { Port = 8443, Type = "transparent", SniDefaultClusterId = "a" };

        StringAssert.Contains(Errors(Config(listener, "a")), "require passthrough: true");
    }

    [TestMethod]
    public void PassthroughNeedsATarget()
    {
        var bare = new ListenerConfig { Port = 8443, Type = "transparent", Passthrough = true };
        StringAssert.Contains(Errors(Config(bare)), "needs sniRoutes, sniDefaultClusterId, or forwardHost");

        var forward = new ListenerConfig
        {
            Port = 8443, Type = "transparent", Passthrough = true, ForwardHost = "127.0.0.1",
        };
        Assert.AreEqual(string.Empty, Errors(Config(forward)));

        var defaultOnly = new ListenerConfig
        {
            Port = 8443, Type = "transparent", Passthrough = true, SniDefaultClusterId = "a",
        };
        Assert.AreEqual(string.Empty, Errors(Config(defaultOnly, "a")));
    }

    [TestMethod]
    public void RouteWithoutClusterId_IsRejected()
    {
        var errors = Errors(Config(Listener(new SniRouteConfig { ServerName = "a.example.com" }), "a"));

        StringAssert.Contains(errors, "sniRoutes[0].clusterId is required");
    }

    [TestMethod]
    public void TimeoutAndProxyProtocolValues_AreBounded()
    {
        var listener = Listener(Route("a.example.com", "a"));
        listener.SniReadTimeoutSeconds = 0;
        listener.SendProxyProtocol = "v1";
        listener.IdleTimeoutSeconds = -1;

        var errors = Errors(Config(listener, "a"));

        StringAssert.Contains(errors, "sniReadTimeoutSeconds must be between 1 and 60");
        StringAssert.Contains(errors, "sendProxyProtocol 'v1'");
        StringAssert.Contains(errors, "idleTimeoutSeconds must be >= 0");

        listener.SniReadTimeoutSeconds = 61;
        StringAssert.Contains(Errors(Config(listener, "a")), "sniReadTimeoutSeconds");
    }

    [TestMethod]
    public void SendProxyProtocolV2_RequiresPassthrough()
    {
        var listener = new ListenerConfig { Port = 8443, SendProxyProtocol = "v2" };

        StringAssert.Contains(Errors(Config(listener)), "sendProxyProtocol v2 requires passthrough");
    }
}
