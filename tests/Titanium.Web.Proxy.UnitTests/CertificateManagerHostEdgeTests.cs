using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     CertificateManager host-name and SAN edge cases.
///     Bug class: IP/IDN/wildcard leaves issued with wrong SAN or shared across hosts.
/// </summary>
[TestClass]
public class CertificateManagerHostEdgeTests
{
    [TestMethod]
    [Timeout(60_000)]
    public void CreateCertificate_IpLiteral_PutsIpAddressSan()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        using var cert = mgr.CreateCertificate("127.0.0.1", false);
        Assert.IsNotNull(cert);
        var san = cert!.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault()
                  ?? new X509SubjectAlternativeNameExtension(cert.Extensions["2.5.29.17"]!.RawData, false);
        var ips = san.EnumerateIPAddresses().Select(ip => ip.ToString()).ToArray();
        CollectionAssert.Contains(ips, "127.0.0.1");
    }

    [TestMethod]
    [Timeout(60_000)]
    public void CreateCertificate_IdnHost_PutsResolvableSan()
    {
        // Bug class: IDN host leaves a SAN browsers cannot match (mojibake / missing punycode).
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        const string unicodeHost = "bücher.example";
        var punycode = new System.Globalization.IdnMapping().GetAscii(unicodeHost);
        using var cert = mgr.CreateCertificate(unicodeHost, false);
        Assert.IsNotNull(cert);
        var san = new X509SubjectAlternativeNameExtension(cert!.Extensions["2.5.29.17"]!.RawData, false);
        var dns = san.EnumerateDnsNames().ToArray();
        Assert.IsTrue(
            dns.Any(d => string.Equals(d, punycode, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(d, unicodeHost, StringComparison.OrdinalIgnoreCase)),
            $"SAN dns={string.Join(",", dns)}; expected punycode '{punycode}' or unicode host");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateServerCertificate_SameHostConcurrent_SharesOneLeaf()
    {
        // Bug class: race creates multiple leaves for one host and exhausts handles.
        // The public path is CreateServerCertificate (pending-task dedupe); CreateCertificate alone does not.
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        mgr.CreateRootCertificate(false);

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => mgr.CreateServerCertificate("race.example"))
            .ToArray();
        var certs = await Task.WhenAll(tasks);
        Assert.IsTrue(certs.All(c => c is not null));
        var thumbs = certs.Select(c => c!.Thumbprint).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.AreEqual(1, thumbs.Length, "concurrent CreateServerCertificate for one host must return the same leaf");
    }

    [TestMethod]
    [Timeout(30_000)]
    public void CreateCertificate_EmptyHost_ThrowsOrReturnsNullWithoutPoisoningCache()
    {
        using var mgr = new CertificateManager(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

        try
        {
            var empty = mgr.CreateCertificate("", false);
            Assert.IsTrue(empty is null || empty.Subject.Length > 0);
            empty?.Dispose();
        }
        catch (ArgumentException)
        {
            // Documented rejection is fine.
        }

        using var ok = mgr.CreateCertificate("after-empty.example", false);
        Assert.IsNotNull(ok);
    }
}
