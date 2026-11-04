using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class UdpListenerValidationTests
{
    private static ClusterConfig Cluster(string id) => new()
    {
        Id = id,
        Destinations = [new DestinationConfig { Id = id + "-d", Address = "127.0.0.1", Port = 5353 }],
    };

    private static ListenerConfig Forward() => new()
    {
        Host = "127.0.0.1",
        Port = 5353,
        Type = "udp",
        ForwardHost = "10.0.0.5",
        ForwardPort = 53,
    };

    private static ListenerConfig ByCluster() => new() { Host = "127.0.0.1", Port = 5353, Type = "udp", ClusterId = "dns" };

    private static TwpConfig Config(ListenerConfig listener) => new()
    {
        Listeners = [listener],
        Clusters = [Cluster("dns")],
    };

    private static string Errors(ListenerConfig listener) => string.Join("\n", TwpConfigValidator.Validate(Config(listener)));

    private static void AssertError(ListenerConfig listener, string expected) =>
        StringAssert.Contains(Errors(listener), expected);

    [TestMethod]
    public void ForwardHostListener_IsAccepted()
    {
        Assert.AreEqual(string.Empty, Errors(Forward()));
    }

    [TestMethod]
    public void ClusterListener_WithEveryLimit_IsAccepted()
    {
        var listener = ByCluster();
        listener.IdleTimeoutSeconds = 5;
        listener.MaxSessions = 500;
        listener.MaxSessionsPerClient = 50;
        listener.MaxDatagramBytes = 1200;
        listener.MaxDatagramsPerSecondPerClient = 200;
        listener.SessionAffinity = "clientEndpoint";

        Assert.AreEqual(string.Empty, Errors(listener));
    }

    [TestMethod]
    public void DocumentedYaml_LoadsAndValidates()
    {
        const string yaml = """
            listeners:
              - host: 0.0.0.0
                port: 5353
                type: udp
                clusterId: dns
                idleTimeoutSeconds: 30
                maxSessions: 10000
                maxSessionsPerClient: 256
                maxDatagramBytes: 1500
                maxDatagramsPerSecondPerClient: 10000
                sessionAffinity: clientEndpoint
            """;

        var config = TwpConfigLoader.LoadYaml(yaml);
        config.Clusters = [Cluster("dns")];

        var listener = config.Listeners.Single();
        Assert.AreEqual("udp", listener.Type);
        Assert.AreEqual("dns", listener.ClusterId);
        Assert.AreEqual(256, listener.MaxSessionsPerClient);
        Assert.AreEqual(string.Empty, string.Join("\n", TwpConfigValidator.Validate(config)));
    }

    [TestMethod]
    public void NeedsExactlyOneDestination()
    {
        var neither = new ListenerConfig { Host = "127.0.0.1", Port = 5353, Type = "udp" };
        AssertError(neither, "exactly one of clusterId or forwardHost");

        var both = Forward();
        both.ClusterId = "dns";
        AssertError(both, "exactly one of clusterId or forwardHost");
    }

    [TestMethod]
    public void UnknownCluster_IsRejected()
    {
        var listener = ByCluster();
        listener.ClusterId = "missing";
        AssertError(listener, "clusterId 'missing' is not a defined cluster");
    }

    [TestMethod]
    public void ForwardHost_NeedsAPort_AndAClusterRefusesOne()
    {
        var noPort = Forward();
        noPort.ForwardPort = null;
        AssertError(noPort, "forwardPort between 1 and 65535");

        var badPort = Forward();
        badPort.ForwardPort = 70000;
        AssertError(badPort, "forwardPort between 1 and 65535");

        var clusterWithPort = ByCluster();
        clusterWithPort.ForwardPort = 53;
        AssertError(clusterWithPort, "forwardPort applies only with forwardHost");
    }

    [TestMethod]
    public void ForwardingBackToTheListener_IsRejected()
    {
        var loop = Forward();
        loop.ForwardHost = "127.0.0.1";
        loop.ForwardPort = 5353;
        AssertError(loop, "point back at this listener");

        var wildcard = Forward();
        wildcard.Host = "0.0.0.0";
        wildcard.ForwardHost = "localhost";
        wildcard.ForwardPort = 5353;
        AssertError(wildcard, "point back at this listener");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-5)]
    [DataRow(86401)]
    public void IdleTimeout_MustBeAtLeastOneSecond(int seconds)
    {
        var listener = ByCluster();
        listener.IdleTimeoutSeconds = seconds;
        AssertError(listener, "idleTimeoutSeconds must be between 1 and 86400");
    }

    [TestMethod]
    public void Limits_AreBounded()
    {
        var zeroSessions = ByCluster();
        zeroSessions.MaxSessions = 0;
        AssertError(zeroSessions, "maxSessions must be positive");

        var hugeSessions = ByCluster();
        hugeSessions.MaxSessions = 2_000_000;
        AssertError(hugeSessions, "maxSessions must be at most 1000000");

        var perClient = ByCluster();
        perClient.MaxSessions = 10;
        perClient.MaxSessionsPerClient = 11;
        AssertError(perClient, "maxSessionsPerClient must not exceed maxSessions");

        var zeroPerClient = ByCluster();
        zeroPerClient.MaxSessionsPerClient = 0;
        AssertError(zeroPerClient, "maxSessionsPerClient must be positive");

        var bigDatagram = ByCluster();
        bigDatagram.MaxDatagramBytes = 65508;
        AssertError(bigDatagram, "maxDatagramBytes must be between 1 and 65507");

        var emptyDatagram = ByCluster();
        emptyDatagram.MaxDatagramBytes = 0;
        AssertError(emptyDatagram, "maxDatagramBytes must be between 1 and 65507");

        var rate = ByCluster();
        rate.MaxDatagramsPerSecondPerClient = 0;
        AssertError(rate, "maxDatagramsPerSecondPerClient must be between 1 and 10000000");
    }

    [TestMethod]
    public void SessionAffinity_OnlyClientEndpoint()
    {
        var listener = ByCluster();
        listener.SessionAffinity = "sourceIp";
        AssertError(listener, "sessionAffinity 'sourceIp' is not clientEndpoint");
    }

    [TestMethod]
    public void ProxyProtocol_IsNotSupported()
    {
        var accept = ByCluster();
        accept.AcceptProxyProtocol = true;
        AssertError(accept, "PROXY protocol is not supported on a udp listener");

        var send = ByCluster();
        send.SendProxyProtocol = "v2";
        AssertError(send, "PROXY protocol is not supported on a udp listener");

        var none = ByCluster();
        none.SendProxyProtocol = "none";
        Assert.AreEqual(string.Empty, Errors(none));
    }

    [TestMethod]
    public void HttpTlsAndMasqueSettings_AreRejected()
    {
        foreach (var listener in new[]
                 {
                     With(l => l.DecryptSsl = true),
                     With(l => l.Passthrough = true),
                     With(l => l.EnableHttp3 = true),
                     With(l => l.Masque = new MasqueListenerConfig()),
                     With(l => l.ClientCertificateMode = "require"),
                     With(l => l.SniDefaultClusterId = "dns"),
                 })
        {
            AssertError(listener, "a udp listener has no HTTP, TLS, passthrough, or MASQUE settings");
        }
    }

    [TestMethod]
    public void Host_MustBeAnIpAddress()
    {
        var listener = ByCluster();
        listener.Host = "example.com";
        AssertError(listener, "host must be an IP address");

        listener.Host = "::";
        Assert.AreEqual(string.Empty, Errors(listener));
    }

    [TestMethod]
    public void UdpOnlySettings_AreRejectedOnOtherListeners()
    {
        var listener = new ListenerConfig { Port = 8080, MaxSessions = 10 };
        AssertError(listener, "apply only to type: udp");

        var cluster = new ListenerConfig { Port = 8080, ClusterId = "dns" };
        AssertError(cluster, "apply only to type: udp");
    }

    [TestMethod]
    public void UdpListener_HasNoAuthentication_SoItWarnsOffLoopback()
    {
        var open = ByCluster();
        open.Host = "0.0.0.0";
        var warnings = string.Join("\n", TwpConfigValidator.CollectWarnings(Config(open)));
        StringAssert.Contains(warnings, "a udp listener has no authentication");

        var loopback = ByCluster();
        Assert.IsFalse(string.Join("\n", TwpConfigValidator.CollectWarnings(Config(loopback)))
            .Contains("no authentication", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ListenerType_Udp_IsKnown()
    {
        Assert.IsFalse(Errors(ByCluster()).Contains("is unknown", StringComparison.Ordinal));
        var unknown = ByCluster();
        unknown.Type = "dgram";
        AssertError(unknown, "is unknown (explicit, transparent, socks, quic, masque, udp)");
    }

    private static ListenerConfig With(Action<ListenerConfig> mutate)
    {
        var listener = ByCluster();
        mutate(listener);
        return listener;
    }
}
