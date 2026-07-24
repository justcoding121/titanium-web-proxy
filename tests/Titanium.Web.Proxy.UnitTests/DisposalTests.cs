using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
/// Regression coverage for issue #902: types that own only managed resources must not
/// run managed cleanup from a finalizer. Explicit Dispose remains the supported path.
/// </summary>
[TestClass]
public class DisposalTests
{
    [TestMethod]
    public void ManagedResourceTypes_HaveNoDeclaredFinalizer()
    {
        AssertHasNoFinalizer(typeof(ProxyServer));
        AssertHasNoFinalizer(typeof(CertificateManager));
        AssertHasNoFinalizer(typeof(CopyStream));
        AssertHasNoFinalizer(typeof(TcpClientConnection));
        AssertHasNoFinalizer(typeof(TcpServerConnection));
        AssertHasNoFinalizer(typeof(TcpConnectionFactory));
        AssertHasNoFinalizer(typeof(EventArguments.SessionEventArgs));
        AssertHasNoFinalizer(typeof(EventArguments.SessionEventArgsBase));
        AssertHasNoFinalizer(typeof(EventArguments.TunnelConnectSessionEventArgs));
    }

    [TestMethod]
    public void ProxyServer_Dispose_IsIdempotent()
    {
        var proxy = new ProxyServer();
        proxy.Dispose();
        proxy.Dispose();
    }

    [TestMethod]
    public void CertificateManager_Dispose_IsIdempotent()
    {
        using var proxy = new ProxyServer();
        var manager = proxy.CertificateManager;
        manager.Dispose();
        manager.Dispose();
    }

    private static void AssertHasNoFinalizer(System.Type type)
    {
        var finalize = type.GetMethod("Finalize",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.IsNull(finalize, $"{type.Name} must not declare a finalizer.");
    }
}
