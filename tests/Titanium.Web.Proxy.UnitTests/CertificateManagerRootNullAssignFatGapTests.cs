#pragma warning disable TWP001
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager edges: assigning null to <see cref="CertificateManager.RootCertificate" />
///     must clear stale signing state without blocking remint, and reassigning the same thumbprint
///     must preserve cached leaves.
/// </summary>
[TestClass]
public class CertificateManagerRootNullAssignFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    /// <summary>
    ///     Bug class: setting RootCertificate to null leaves a stale leaf cache signed by the old root.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task RootCertificate_AssignNull_ClearsRootThenRemintsLeaf()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        var thumbBefore = mgr.RootCertificate!.Thumbprint;
        using var first = await mgr.CreateServerCertificate("null-root-wave26.example");
        Assert.IsNotNull(first);
        var leafThumbBefore = first!.Thumbprint;

        mgr.RootCertificate = null;
        Assert.IsNull(mgr.RootCertificate);

        mgr.CreateRootCertificate(false);
        Assert.IsNotNull(mgr.RootCertificate);
        Assert.AreNotEqual(thumbBefore, mgr.RootCertificate!.Thumbprint, StringComparer.OrdinalIgnoreCase);
        using var second = await mgr.CreateServerCertificate("null-root-wave26.example");
        Assert.IsNotNull(second);
        Assert.AreNotEqual(leafThumbBefore, second!.Thumbprint, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Bug class: reloading the same persisted root thumbprint wipes the leaf cache on startup.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task RootCertificate_SameThumbprintReassign_ReusesCachedLeaf()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        var root = mgr.RootCertificate;
        using var leaf = await mgr.CreateServerCertificate("same-thumb-wave26.example");
        Assert.IsNotNull(leaf);
        var expected = leaf!.Thumbprint;

        mgr.RootCertificate = root;
        var again = await mgr.CreateServerCertificate("same-thumb-wave26.example");
        Assert.IsNotNull(again);
        Assert.AreEqual(expected, again!.Thumbprint, StringComparer.OrdinalIgnoreCase);
        again.Dispose();
    }
}
#pragma warning restore TWP001
