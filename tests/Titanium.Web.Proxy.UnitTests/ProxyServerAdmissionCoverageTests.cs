using System;
using System.Net;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Coverage for ProxyServer admission gates and system-proxy endpoint validation helpers.
/// </summary>
[TestClass]
public class ProxyServerAdmissionCoverageTests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static bool TryAdmitGlobal(ProxyServer proxy, PolicyMode mode)
        => (bool)typeof(ProxyServer).GetMethod("TryAdmitGlobal", PrivateInstance)!
            .Invoke(proxy, [mode])!;

    private static bool TryAdmitClientConnection(ProxyServer proxy, ProxyEndPoint endPoint)
        => (bool)typeof(ProxyServer).GetMethod("TryAdmitClientConnection", PrivateInstance)!
            .Invoke(proxy, [endPoint])!;

    private static void ReleaseClientConnection(ProxyServer proxy, ProxyEndPoint endPoint)
        => typeof(ProxyServer).GetMethod("ReleaseClientConnection", PrivateInstance)!
            .Invoke(proxy, [endPoint]);

    private static void ValidateEndPointAsSystemProxy(ProxyServer proxy, ExplicitProxyEndPoint endPoint)
        => typeof(ProxyServer).GetMethod("ValidateEndPointAsSystemProxy", PrivateInstance)!
            .Invoke(proxy, [endPoint]);

    private static void ClearEndpointSystemProxyFlags(ProxyServer proxy, ProxyProtocolType protocolType)
        => typeof(ProxyServer).GetMethod("ClearEndpointSystemProxyFlags", PrivateInstance)!
            .Invoke(proxy, [protocolType]);

    [TestMethod]
    public void TryAdmitGlobal_Enforce_RejectsAtLimit()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.MaxConcurrentClientConnections = 1;
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce;

        Assert.IsTrue(TryAdmitGlobal(proxy, PolicyMode.Enforce));
        Assert.AreEqual(1, proxy.AdmittedClientConnectionCount);
        Assert.IsFalse(TryAdmitGlobal(proxy, PolicyMode.Enforce));
        Assert.AreEqual(1, proxy.AdmittedClientConnectionCount);
    }

    [TestMethod]
    public void TryAdmitGlobal_Observe_AdmitsPastLimit()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.MaxConcurrentClientConnections = 1;
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce
            .With(PolicyFamily.AdmissionControl, PolicyMode.Observe);

        Assert.IsTrue(TryAdmitGlobal(proxy, PolicyMode.Observe));
        Assert.IsTrue(TryAdmitGlobal(proxy, PolicyMode.Observe));
        Assert.AreEqual(2, proxy.AdmittedClientConnectionCount);
    }

    [TestMethod]
    public void TryAdmitGlobal_Disabled_IgnoresLimit()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.MaxConcurrentClientConnections = 0;
        Assert.IsTrue(TryAdmitGlobal(proxy, PolicyMode.Disabled));
        Assert.IsTrue(TryAdmitGlobal(proxy, PolicyMode.Disabled));
        Assert.AreEqual(2, proxy.AdmittedClientConnectionCount);
    }

    [TestMethod]
    public void TryAdmitClientConnection_GlobalReject_IncrementsGlobalCounter()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.MaxConcurrentClientConnections = 1;
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce;
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);

        Assert.IsTrue(TryAdmitClientConnection(proxy, ep));
        Assert.IsFalse(TryAdmitClientConnection(proxy, ep));
        Assert.AreEqual(1, proxy.GlobalAdmissionRejectionCount);
        Assert.AreEqual(0, proxy.EndpointAdmissionRejectionCount);
        Assert.AreEqual(1, proxy.AdmittedClientConnectionCount);

        ReleaseClientConnection(proxy, ep);
        Assert.AreEqual(0, proxy.AdmittedClientConnectionCount);
        Assert.AreEqual(0, ep.AdmittedClientCount);
    }

    [TestMethod]
    public void TryAdmitClientConnection_EndpointReject_IncrementsEndpointCounter()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.MaxConcurrentClientConnections = null;
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce;
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            MaxConcurrentClients = 0
        };

        Assert.IsFalse(TryAdmitClientConnection(proxy, ep));
        Assert.AreEqual(0, proxy.GlobalAdmissionRejectionCount);
        Assert.AreEqual(1, proxy.EndpointAdmissionRejectionCount);
        Assert.AreEqual(0, ep.AdmittedClientCount);
        // Global slot rolled back on endpoint rejection.
        Assert.AreEqual(0, proxy.AdmittedClientConnectionCount);

        ep.MaxConcurrentClients = 1;
        Assert.IsTrue(TryAdmitClientConnection(proxy, ep));
        Assert.IsFalse(TryAdmitClientConnection(proxy, ep));
        Assert.AreEqual(2, proxy.EndpointAdmissionRejectionCount);
        Assert.AreEqual(1, ep.AdmittedClientCount);
        Assert.AreEqual(1, proxy.AdmittedClientConnectionCount);

        ReleaseClientConnection(proxy, ep);
        Assert.AreEqual(0, ep.AdmittedClientCount);
        Assert.AreEqual(0, proxy.AdmittedClientConnectionCount);
    }

    [TestMethod]
    public void ValidateEndPointAsSystemProxy_Null_Throws()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ex = Assert.ThrowsExactly<TargetInvocationException>(() =>
            ValidateEndPointAsSystemProxy(proxy, null!));
        Assert.IsInstanceOfType(ex.InnerException, typeof(ArgumentNullException));
    }

    [TestMethod]
    public void ValidateEndPointAsSystemProxy_NotAdded_Throws()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var ex = Assert.ThrowsExactly<TargetInvocationException>(() =>
            ValidateEndPointAsSystemProxy(proxy, ep));
        Assert.IsInstanceOfType(ex.InnerException, typeof(InvalidOperationException));
        StringAssert.Contains(ex.InnerException!.Message, "not added");
    }

    [TestMethod]
    public void ValidateEndPointAsSystemProxy_NotRunning_Throws()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(ep);
        var ex = Assert.ThrowsExactly<TargetInvocationException>(() =>
            ValidateEndPointAsSystemProxy(proxy, ep));
        Assert.IsInstanceOfType(ex.InnerException, typeof(InvalidOperationException));
        StringAssert.Contains(ex.InnerException!.Message, "before proxy has been started");
    }

    [TestMethod]
    public void ClearEndpointSystemProxyFlags_ClearsOnlyRequestedProtocols()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            IsSystemHttpProxy = true,
            IsSystemHttpsProxy = true
        };
        proxy.AddEndPoint(ep);

        ClearEndpointSystemProxyFlags(proxy, ProxyProtocolType.Http);
        Assert.IsFalse(ep.IsSystemHttpProxy);
        Assert.IsTrue(ep.IsSystemHttpsProxy);

        ClearEndpointSystemProxyFlags(proxy, ProxyProtocolType.Https);
        Assert.IsFalse(ep.IsSystemHttpsProxy);

        ep.IsSystemHttpProxy = true;
        ep.IsSystemHttpsProxy = true;
        ClearEndpointSystemProxyFlags(proxy, ProxyProtocolType.AllHttp);
        Assert.IsFalse(ep.IsSystemHttpProxy);
        Assert.IsFalse(ep.IsSystemHttpsProxy);

        ep.IsSystemHttpProxy = true;
        ClearEndpointSystemProxyFlags(proxy, (ProxyProtocolType)0);
        Assert.IsTrue(ep.IsSystemHttpProxy);
    }
}
