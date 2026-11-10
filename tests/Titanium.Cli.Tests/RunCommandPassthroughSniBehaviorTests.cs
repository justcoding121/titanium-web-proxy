using System.Net;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Tests;

/// <summary>
/// Passthrough SNI wiring on transparent listeners: routes, default cluster, timeouts, and PROXY protocol v2.
/// </summary>
[TestClass]
public class RunCommandPassthroughSniBehaviorTests
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

    // Bug class: a passthrough listener with SNI routes still decrypts / ignores the routes, so L4 routing never fires.
    [TestMethod]
    public void CreateTransparentEndPoint_PassthroughSni_WiresRoutesTimeoutsAndProxyProtocolV2()
    {
        var ep = (TransparentProxyEndPoint)Invoke(
            "CreateTransparentEndPoint",
            IPAddress.Loopback,
            new ListenerConfig
            {
                Port = 0,
                Passthrough = true,
                SniDefaultClusterId = "fallback",
                SniReadTimeoutSeconds = 3,
                IdleTimeoutSeconds = 45,
                SendProxyProtocol = "v2",
                SniRoutes =
                [
                    new SniRouteConfig { ServerName = "a.example", ClusterId = "c-a" },
                    new SniRouteConfig { ServerName = null, ClusterId = null },
                ],
            })!;

        Assert.IsTrue(ep.Passthrough);
        Assert.AreEqual("fallback", ep.PassthroughDefaultClusterId);
        Assert.AreEqual(TimeSpan.FromSeconds(3), ep.PassthroughSniReadTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(45), ep.PassthroughIdleTimeout);
        Assert.AreEqual(PassthroughProxyProtocolMode.V2, ep.PassthroughProxyProtocol);
        Assert.IsNotNull(ep.PassthroughRoutes);
        Assert.AreEqual(2, ep.PassthroughRoutes!.Count);
        Assert.AreEqual("a.example", ep.PassthroughRoutes[0].ServerName);
        Assert.AreEqual("c-a", ep.PassthroughRoutes[0].ClusterId);
        Assert.AreEqual(string.Empty, ep.PassthroughRoutes[1].ServerName);
        Assert.AreEqual(string.Empty, ep.PassthroughRoutes[1].ClusterId);
    }

    // Bug class: non-passthrough listeners still get SNI route tables applied (or ResolveListenerKind stays explicit
    // when only SNI cluster routing is set).
    [TestMethod]
    public void CreateTransparentEndPoint_WithoutPassthrough_DoesNotApplySniRoutes()
    {
        var ep = (TransparentProxyEndPoint)Invoke(
            "CreateTransparentEndPoint",
            IPAddress.Loopback,
            new ListenerConfig
            {
                Port = 0,
                Passthrough = false,
                SniDefaultClusterId = "should-ignore",
                SniRoutes = [new SniRouteConfig { ServerName = "x", ClusterId = "c" }],
                SendProxyProtocol = "v2",
            })!;

        Assert.IsFalse(ep.Passthrough);
        Assert.IsNull(ep.PassthroughRoutes);
        Assert.IsTrue(string.IsNullOrEmpty(ep.PassthroughDefaultClusterId));
        Assert.AreNotEqual(PassthroughProxyProtocolMode.V2, ep.PassthroughProxyProtocol);
    }

    [TestMethod]
    public void ResolveListenerKind_SniClusterRouting_ImpliesTransparentWithoutForwardHost()
    {
        Assert.AreEqual(
            "transparent",
            (string)Invoke("ResolveListenerKind", new ListenerConfig { SniDefaultClusterId = "c1" })!);
        Assert.AreEqual(
            "transparent",
            (string)Invoke(
                "ResolveListenerKind",
                new ListenerConfig { SniRoutes = [new SniRouteConfig { ServerName = "a", ClusterId = "c" }] })!);
        Assert.AreEqual("explicit", (string)Invoke("ResolveListenerKind", new ListenerConfig())!);
    }

    // Bug class: TerminateTls + client-certificate mode knobs are dropped on transparent endpoints.
    [TestMethod]
    public void CreateTransparentEndPoint_TerminateTlsAndClientCertificateModes()
    {
        var require = (TransparentProxyEndPoint)Invoke(
            "CreateTransparentEndPoint",
            IPAddress.Loopback,
            new ListenerConfig
            {
                Port = 0,
                TerminateTls = true,
                TlsHandshakeTimeoutSeconds = 7,
                MaxConcurrentHandshakes = 12,
                Alpn = ["h2", "http/1.1"],
                ClientCertificateMode = "require",
                AcceptProxyProtocol = true,
                TrustedProxyProtocolSources = ["127.0.0.1/32"],
            })!;

        Assert.IsTrue(require.TerminateTls);
        Assert.AreEqual(TimeSpan.FromSeconds(7), require.TlsHandshakeTimeout);
        Assert.AreEqual(12, require.MaxConcurrentTlsHandshakes);
        CollectionAssert.AreEqual(new[] { "h2", "http/1.1" }, require.TerminateTlsAlpn!.ToArray());
        Assert.AreEqual(ClientCertificateMode.Require, require.ClientCertificateMode);
        Assert.IsTrue(require.AcceptProxyProtocol);
        CollectionAssert.AreEqual(new[] { "127.0.0.1/32" }, require.TrustedProxyProtocolSources!.ToArray());

        var allow = (TransparentProxyEndPoint)Invoke(
            "CreateTransparentEndPoint",
            IPAddress.Loopback,
            new ListenerConfig { Port = 0, ClientCertificateMode = "allow" })!;
        Assert.AreEqual(ClientCertificateMode.Allow, allow.ClientCertificateMode);

        var none = (TransparentProxyEndPoint)Invoke(
            "CreateTransparentEndPoint",
            IPAddress.Loopback,
            new ListenerConfig { Port = 0, ClientCertificateMode = "bogus" })!;
        Assert.AreEqual(ClientCertificateMode.None, none.ClientCertificateMode);
    }

    private static object? Invoke(string name, params object?[] args)
    {
        var methods = typeof(RunCommand).GetMethods(Priv).Where(m => m.Name == name).ToArray();
        foreach (var method in methods)
        {
            try
            {
                var parameters = method.GetParameters();
                var full = args.Length < parameters.Length && parameters.Skip(args.Length).All(static p => p.IsOptional)
                    ? [.. args, .. parameters.Skip(args.Length).Select(static p => p.DefaultValue)]
                    : args;
                return method.Invoke(null, full);
            }
            catch (TargetParameterCountException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        throw new InvalidOperationException(name);
    }
}
