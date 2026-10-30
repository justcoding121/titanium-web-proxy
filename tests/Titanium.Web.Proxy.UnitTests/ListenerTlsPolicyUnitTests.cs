using System;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ListenerTlsPolicyUnitTests
{
    private const SslProtocols Tls12And13 = SslProtocols.Tls12 | SslProtocols.Tls13;

    [TestMethod]
    public void ApplyMinimumProtocol_UnsetMinimum_KeepsTheSupportedList()
    {
        Assert.AreEqual(Tls12And13, ProxyServer.ApplyMinimumProtocol(Tls12And13, SslProtocols.None));
        Assert.AreEqual(SslProtocols.Tls13, ProxyServer.ApplyMinimumProtocol(SslProtocols.Tls13, SslProtocols.None));
    }

    [TestMethod]
    public void ApplyMinimumProtocol_TrimsEverythingBelowTheMinimum_AndNeverWidens()
    {
        Assert.AreEqual(SslProtocols.Tls13, ProxyServer.ApplyMinimumProtocol(Tls12And13, SslProtocols.Tls13));
        Assert.AreEqual(Tls12And13, ProxyServer.ApplyMinimumProtocol(Tls12And13, SslProtocols.Tls12));
        Assert.AreEqual(SslProtocols.Tls12, ProxyServer.ApplyMinimumProtocol(SslProtocols.Tls12, SslProtocols.Tls12));
#pragma warning disable SYSLIB0039 // Legacy protocols are the input under test.
        var legacy = SslProtocols.Tls | SslProtocols.Tls11 | Tls12And13;
#pragma warning restore SYSLIB0039
        Assert.AreEqual(Tls12And13, ProxyServer.ApplyMinimumProtocol(legacy, SslProtocols.Tls12));
        Assert.AreEqual(SslProtocols.Tls13, ProxyServer.ApplyMinimumProtocol(legacy, SslProtocols.Tls13));
    }

    [TestMethod]
    public void ApplyMinimumProtocol_MinimumAboveEverySupported_NeverFallsBackToTheOsDefault()
    {
        Assert.AreEqual(SslProtocols.Tls13, ProxyServer.ApplyMinimumProtocol(SslProtocols.Tls12, SslProtocols.Tls13));
        Assert.AreEqual(Tls12And13, ProxyServer.ApplyMinimumProtocol(SslProtocols.None, SslProtocols.Tls12));
    }

    [TestMethod]
    public void ApplyListenerTlsPolicy_DefaultsLeaveTheOptionsAsToday()
    {
        using var proxy = new ProxyServer(false, false, false);
        var options = new SslServerAuthenticationOptions();

        proxy.ApplyListenerTlsPolicy(options);

        Assert.AreEqual(proxy.SupportedSslProtocols, options.EnabledSslProtocols);
        Assert.IsNull(options.CipherSuitesPolicy);
    }

    [TestMethod]
    public void ApplyListenerTlsPolicy_MinVersion_NarrowsTheEnabledProtocols()
    {
        using var proxy = new ProxyServer(false, false, false) { ListenerMinimumSslProtocol = SslProtocols.Tls13 };
        var options = new SslServerAuthenticationOptions();

        proxy.ApplyListenerTlsPolicy(options);

        Assert.AreEqual(SslProtocols.Tls13, options.EnabledSslProtocols);
        Assert.AreEqual(Tls12And13, proxy.SupportedSslProtocols, "outbound and shared settings are untouched");
    }

    [TestMethod]
    public void ApplyListenerTlsPolicy_CipherSuites_SetAPolicyWhereTheOsHasOne()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            ListenerCipherSuites = [TlsCipherSuite.TLS_AES_128_GCM_SHA256],
        };
        var options = new SslServerAuthenticationOptions();

        proxy.ApplyListenerTlsPolicy(options);

        if (OperatingSystem.IsWindows())
        {
            Assert.IsNull(options.CipherSuitesPolicy, "Windows has no cipher suite policy");
            return;
        }

        Assert.IsNotNull(options.CipherSuitesPolicy);
        CollectionAssert.AreEqual(
            new[] { TlsCipherSuite.TLS_AES_128_GCM_SHA256 }, options.CipherSuitesPolicy.AllowedCipherSuites.ToArray());

        // The policy is built once per setting, not once per handshake.
        var again = new SslServerAuthenticationOptions();
        proxy.ApplyListenerTlsPolicy(again);
        Assert.AreSame(options.CipherSuitesPolicy, again.CipherSuitesPolicy);
    }

    [TestMethod]
    public void OcspStapling_DefaultsOff_AndForwardsToTheCertificateManager()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsFalse(proxy.OcspStapling);

        proxy.OcspStapling = true;

        Assert.IsTrue(proxy.CertificateManager.OcspStapling);
    }

    [TestMethod]
    public void OcspStapling_Toggle_RebuildsTheCachedCertificateContext()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        using var leaf = mgr.CreateServerCertificate("ocsp.example").GetAwaiter().GetResult()!;

        var offline = mgr.CreateSslCertificateContext(leaf);
        Assert.AreSame(offline, mgr.CreateSslCertificateContext(leaf), "unchanged setting reuses the context");

        mgr.OcspStapling = true;
        var online = mgr.CreateSslCertificateContext(leaf);

        Assert.AreNotSame(offline, online);
        Assert.AreEqual(leaf.Thumbprint, online.TargetCertificate.Thumbprint);
        Assert.AreSame(online, mgr.CreateSslCertificateContext(leaf));
    }
}
