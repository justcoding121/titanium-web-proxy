using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Certificates;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ClientCertificateTrustAnchorTests
{
    [TestMethod]
    public void CustomRoot_AcceptsAClientAuthLeaf_AndRefusesTheWrongAuthority()
    {
        using var caKey = RSA.Create(2048);
        using var ca = CreateCertificate(caKey, "CN=Test CA", caKey, isCa: true, clientAuth: false);
        using var otherKey = RSA.Create(2048);
        using var other = CreateCertificate(otherKey, "CN=Other CA", otherKey, isCa: true, clientAuth: false);
        using var leafKey = RSA.Create(2048);
        using var leaf = CreateCertificate(leafKey, "CN=client.example", caKey, isCa: false, clientAuth: true, issuer: ca);
        using var foreign = CreateCertificate(leafKey, "CN=foreign.example", otherKey, isCa: false, clientAuth: true, issuer: other);
        using var noEku = CreateCertificate(leafKey, "CN=no-eku.example", caKey, isCa: false, clientAuth: false, issuer: ca);

        var roots = new X509Certificate2Collection { ca };
        Assert.IsTrue(ClientCertificateHandshake.ValidateWithRoots(leaf, null, ClientCertificateMode.Require, roots), "trusted client-auth leaf");
        Assert.IsTrue(ClientCertificateHandshake.ValidateWithRoots(null, null, ClientCertificateMode.Allow, roots), "allow missing");
        Assert.IsFalse(ClientCertificateHandshake.ValidateWithRoots(null, null, ClientCertificateMode.Require, roots), "require missing");
        Assert.IsFalse(ClientCertificateHandshake.ValidateWithRoots(foreign, null, ClientCertificateMode.Allow, roots), "other authority");
        Assert.IsFalse(ClientCertificateHandshake.ValidateWithRoots(noEku, null, ClientCertificateMode.Require, roots), "missing client-auth EKU");
    }

    [TestMethod]
    public async Task CustomRoot_DoesNotDownloadTheAuthorityInformationAccessUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = 0;
        using var stop = new CancellationTokenSource();
        var accept = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref accepted);
                }
                catch (Exception) when (stop.IsCancellationRequested)
                {
                    return;
                }
            }
        });

        using var rootKey = RSA.Create(2048);
        using var root = CreateCertificate(rootKey, "CN=AIA Root", rootKey, isCa: true, clientAuth: false);
        using var intermediateKey = RSA.Create(2048);
        using var intermediatePublic = CreateCertificate(intermediateKey, "CN=AIA Intermediate", rootKey, isCa: true, clientAuth: false, issuer: root);
        using var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=aia.example", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false));
        request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(
            ocspUris: null, caIssuersUris: [$"http://127.0.0.1:{port}/ca"], critical: false));
        using var leaf = request.Create(intermediate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid().ToByteArray());

        ClientCertificateHandshake.ValidateWithRoots(leaf, null, ClientCertificateMode.Require, new X509Certificate2Collection { root });
        await Task.Delay(400);
        stop.Cancel();
        listener.Stop();
        await accept.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, Volatile.Read(ref accepted));
    }

    private static X509Certificate2 CreateCertificate(RSA subjectKey, string subject, RSA issuerKey, bool isCa,
        bool clientAuth, X509Certificate2? issuer = null)
    {
        var request = new CertificateRequest(subject, subjectKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCa, false, 0, true));
        if (clientAuth)
        {
            var eku = new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        }

        if (isCa)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, false));
        }

        var notBefore = DateTimeOffset.UtcNow.AddDays(-2);
        var notAfter = DateTimeOffset.UtcNow.AddDays(isCa ? 30 : 2);
        return issuer is null
            ? request.CreateSelfSigned(notBefore, notAfter)
            : request.Create(issuer, notBefore, notAfter, Guid.NewGuid().ToByteArray());
    }
}
