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
        AssertDisposed(proxy);
        Assert.IsFalse(proxy.ProxyRunning);
    }

    [TestMethod]
    public void CertificateManager_Dispose_IsIdempotent()
    {
        using var proxy = new ProxyServer();
        var manager = proxy.CertificateManager;
        manager.Dispose();
        manager.Dispose();
        AssertDisposed(manager);
    }

    [TestMethod]
    public void TcpConnectionFactory_Dispose_IsIdempotent_And_DoesNotThrow()
    {
        // Regression: @lock and _cleanupCts must NOT be explicitly disposed because the background
        // cleanup task may still be accessing them at the time Dispose() is called, which would
        // cause ObjectDisposedException from SemaphoreSlim.WaitAsync / CTS.Token.
        // Dispose() must complete without throwing and must be idempotent.
        var factory = new Network.Tcp.TcpConnectionFactory(new ProxyServer());
        factory.Dispose();
        // A second disposal must not throw (idempotent guard).
        factory.Dispose();
        AssertDisposed(factory);
    }

    private static void AssertDisposed(object instance)
    {
        var field = instance.GetType().GetField("disposed",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(field, $"{instance.GetType().Name} must expose a disposed guard field.");
        Assert.IsTrue((bool)field.GetValue(instance)!,
            $"{instance.GetType().Name} should be marked disposed after Dispose().");
    }

    private static void AssertHasNoFinalizer(System.Type type)
    {
        var finalize = type.GetMethod("Finalize",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.IsNull(finalize, $"{type.Name} must not declare a finalizer.");
    }
}
