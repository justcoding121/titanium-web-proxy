using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Transforms;
using Titanium.Web.Proxy.UnitTests.Support;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class ForwardedHeadersApplierTests
{
    private static string? Header(SessionEventArgs session, string name)
    {
        var found = session.HttpClient.Request.Headers.GetHeaders(name);
        return found is { Count: > 0 } ? found[0].Value : null;
    }

    private static SessionEventArgs Session(ProxyServer proxy, string client = "127.0.0.1", string? inboundFor = null)
    {
        var session = TestSessions.Create(proxy, client);
        session.HttpClient.Request.Host = "app.test";
        if (inboundFor is not null)
        {
            session.HttpClient.Request.Headers.AddHeader("X-Forwarded-For", inboundFor);
        }

        return session;
    }

    [TestMethod]
    public void Set_WritesTheClientAddressProtoAndHost()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var session = Session(proxy, inboundFor: "9.9.9.9");
        ForwardedHeadersApplier.Apply(session, new ForwardedHeadersConfig
        {
            XForwardedFor = ForwardedHeaderAction.Set,
            XForwardedProto = ForwardedHeaderAction.Set,
            XForwardedHost = ForwardedHeaderAction.Set,
        });
        Assert.AreEqual("127.0.0.1", Header(session, "X-Forwarded-For"));
        Assert.AreEqual("http", Header(session, "X-Forwarded-Proto"));
        Assert.AreEqual("app.test", Header(session, "X-Forwarded-Host"));
    }

    [TestMethod]
    public void Append_FromAnUntrustedPeer_ReplacesTheInboundValue()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var session = Session(proxy, inboundFor: "9.9.9.9");
        ForwardedHeadersApplier.Apply(session, new ForwardedHeadersConfig { XForwardedFor = ForwardedHeaderAction.Append });
        Assert.AreEqual("127.0.0.1", Header(session, "X-Forwarded-For"));
    }

    [TestMethod]
    public void Append_FromATrustedPeer_ExtendsTheChain()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var config = new ForwardedHeadersConfig
        {
            XForwardedFor = ForwardedHeaderAction.Append,
            TrustedProxies = ["127.0.0.1"],
        };
        var chained = Session(proxy, inboundFor: "9.9.9.9");
        ForwardedHeadersApplier.Apply(chained, config);
        Assert.AreEqual("9.9.9.9, 127.0.0.1", Header(chained, "X-Forwarded-For"));

        var first = Session(proxy);
        ForwardedHeadersApplier.Apply(first, config);
        Assert.AreEqual("127.0.0.1", Header(first, "X-Forwarded-For"));
    }

    [DataTestMethod]
    [DataRow("127.0.0.0/8", true)]
    [DataRow("0.0.0.0/0", true)]
    [DataRow("10.0.0.0/8", false)]
    [DataRow("not-an-address/8", false)]
    [DataRow("127.0.0.0/x", false)]
    [DataRow("::1", false)]
    public void TrustedProxies_AcceptsCidrRulesAndIgnoresBadOnes(string rule, bool trusted)
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var session = Session(proxy, inboundFor: "9.9.9.9");
        ForwardedHeadersApplier.Apply(session, new ForwardedHeadersConfig
        {
            XForwardedFor = ForwardedHeaderAction.Append,
            TrustedProxies = new List<string> { rule },
        });
        Assert.AreEqual(trusted ? "9.9.9.9, 127.0.0.1" : "127.0.0.1", Header(session, "X-Forwarded-For"));
    }

    [TestMethod]
    public void IPv6Peer_IsOnlyTrustedByAnExactRule()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var config = new ForwardedHeadersConfig
        {
            XForwardedFor = ForwardedHeaderAction.Append,
            TrustedProxies = ["10.0.0.0/8", "::1"],
        };
        var session = Session(proxy, client: "::1", inboundFor: "9.9.9.9");
        ForwardedHeadersApplier.Apply(session, config);
        Assert.AreEqual("9.9.9.9, ::1", Header(session, "X-Forwarded-For"));
    }

    [TestMethod]
    public void RemoveAndOff_AreHonoured()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var session = Session(proxy, inboundFor: "9.9.9.9");
        ForwardedHeadersApplier.Apply(session, new ForwardedHeadersConfig
        {
            XForwardedFor = ForwardedHeaderAction.Remove,
            XForwardedProto = ForwardedHeaderAction.Off,
        });
        Assert.IsNull(Header(session, "X-Forwarded-For"));
        Assert.IsNull(Header(session, "X-Forwarded-Proto"));
    }
}
