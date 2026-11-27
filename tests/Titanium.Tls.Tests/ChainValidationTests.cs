using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class ChainValidationTests
{
    private static readonly DateTimeOffset NotBefore = DateTimeOffset.UtcNow.AddDays(-2);
    private static readonly DateTimeOffset NotAfter = DateTimeOffset.UtcNow.AddYears(1);
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";

    [TestMethod]
    public void TrustedHierarchyMatchesTheHostname()
    {
        using X509Certificate2 root = Authority("CN=Test Root");
        using X509Certificate2 intermediate = Issue(root, "CN=Test Intermediate", ca: true, dns: null, eku: null, NotBefore, NotAfter);
        using X509Certificate2 leaf = Issue(intermediate, "CN=other", ca: false, dns: "localhost", eku: ServerAuth, NotBefore, NotAfter);
        var trust = new X509Certificate2Collection(root);
        using TlsCertificateCheck check = TlsChainValidator.Evaluate(CertificateList(null, null, leaf, intermediate), "localhost", trust, serverCertificate: true);
        Assert.AreEqual(SslPolicyErrors.None, check.Errors, Status(check.Chain));
    }

    [TestMethod]
    public void NameInTheCommonNameIsIgnoredWhenASanExists()
    {
        using X509Certificate2 root = Authority("CN=Test Root");
        using X509Certificate2 leaf = Issue(root, "CN=localhost", ca: false, dns: "www.example.com", eku: ServerAuth, NotBefore, NotAfter);
        var trust = new X509Certificate2Collection(root);
        using TlsCertificateCheck check = TlsChainValidator.Evaluate(CertificateList(null, null, leaf), "localhost", trust, serverCertificate: true);
        Assert.AreEqual(SslPolicyErrors.RemoteCertificateNameMismatch, check.Errors, Status(check.Chain));
    }

    [TestMethod]
    public void CommonNameIsUsedWhenTheCertificateHasNoSan()
    {
        using X509Certificate2 root = Authority("CN=Test Root");
        using X509Certificate2 leaf = Issue(root, "CN=localhost", ca: false, dns: null, eku: ServerAuth, NotBefore, NotAfter);
        var trust = new X509Certificate2Collection(root);
        using TlsCertificateCheck check = TlsChainValidator.Evaluate(CertificateList(null, null, leaf), "localhost", trust, serverCertificate: true);
        Assert.AreEqual(SslPolicyErrors.None, check.Errors, Status(check.Chain));
    }

    [TestMethod]
    public void ExpiredWrongEkuUntrustedAndIncompleteChainsFail()
    {
        using X509Certificate2 root = Authority("CN=Test Root");
        using X509Certificate2 intermediate = Issue(root, "CN=Test Intermediate", ca: true, dns: null, eku: null, NotBefore, NotAfter);
        using X509Certificate2 expired = Issue(intermediate, "CN=localhost", ca: false, dns: "localhost", eku: ServerAuth, NotBefore, NotBefore.AddDays(1));
        using X509Certificate2 wrongEku = Issue(intermediate, "CN=localhost", ca: false, dns: "localhost", eku: "1.3.6.1.5.5.7.3.3", NotBefore, NotAfter);
        using X509Certificate2 selfSigned = Authority("CN=localhost");
        using X509Certificate2 missingLeaf = Issue(intermediate, "CN=localhost", ca: false, dns: "localhost", eku: ServerAuth, NotBefore, NotAfter);
        var trust = new X509Certificate2Collection(root);
        using TlsCertificateCheck expiredCheck = TlsChainValidator.Evaluate(CertificateList(null, null, expired, intermediate), "localhost", trust, serverCertificate: true);
        using TlsCertificateCheck ekuCheck = TlsChainValidator.Evaluate(CertificateList(null, null, wrongEku, intermediate), "localhost", trust, serverCertificate: true);
        using TlsCertificateCheck untrusted = TlsChainValidator.Evaluate(CertificateList(null, null, selfSigned), "localhost", trust, serverCertificate: true);
        using TlsCertificateCheck missing = TlsChainValidator.Evaluate(CertificateList(null, null, missingLeaf), "localhost", trust, serverCertificate: true);
        Assert.AreEqual(SslPolicyErrors.RemoteCertificateChainErrors, expiredCheck.Errors, Status(expiredCheck.Chain));
        Assert.AreEqual(SslPolicyErrors.RemoteCertificateChainErrors, ekuCheck.Errors, Status(ekuCheck.Chain));
        Assert.AreEqual(SslPolicyErrors.RemoteCertificateChainErrors, untrusted.Errors, Status(untrusted.Chain));
        Assert.AreEqual(SslPolicyErrors.RemoteCertificateChainErrors, missing.Errors, Status(missing.Chain));
    }

    [TestMethod]
    public void EmptyListIsNotAvailableAndStapledBytesAreCopied()
    {
        using TlsCertificateCheck empty = TlsChainValidator.Evaluate([], "localhost", new X509Certificate2Collection(), serverCertificate: true);
        Assert.AreEqual(SslPolicyErrors.RemoteCertificateNotAvailable, empty.Errors);
        using X509Certificate2 root = Authority("CN=Test Root");
        using X509Certificate2 leaf = Issue(root, "CN=localhost", ca: false, dns: "localhost", eku: ServerAuth, NotBefore, NotAfter);
        byte[] status = [1, 2, 3];
        byte[] timestamps = [9, 8];
        var trust = new X509Certificate2Collection(root);
        using TlsCertificateCheck check = TlsChainValidator.Evaluate(CertificateList(status, timestamps, leaf), "localhost", trust, serverCertificate: true);
        CollectionAssert.AreEqual(status, check.StapledStatus);
        CollectionAssert.AreEqual(timestamps, check.SignedCertificateTimestamps);
        Assert.AreEqual(SslPolicyErrors.None, check.Errors, Status(check.Chain));
    }

    [TestMethod]
    public void HandshakeRecordsChainErrorsForASelfSignedCertificate()
    {
        using HandshakePair pair = HandshakePair.Run();
        Assert.IsTrue(pair.Client.ServerCertificateErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [TestMethod]
    public void PolicyErrorsMatchSslStream()
    {
        using X509Certificate2 certificate = Authority("CN=localhost");
        SslPolicyErrors streamErrors = CaptureSslStreamErrors(certificate, "localhost");
        using TlsCertificateCheck check = TlsChainValidator.Evaluate(CertificateList(null, null, certificate), "localhost", trustAnchors: null, serverCertificate: true);
        Assert.AreEqual(streamErrors, check.Errors);
    }

    [TestMethod]
    public void RejectedSchemesDoNotVerify()
    {
        byte[] content = "handshake"u8.ToArray();
        using ECDsa p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using X509Certificate2 ecdsa = SelfSigned(p256, "CN=localhost");
        using X509Certificate2 wider = SelfSigned(p384, "CN=localhost");
        byte[] ecdsaSignature = p256.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.IsTrue(TlsCertificateVerifier.Verify(ecdsa, 0x0403, content, ecdsaSignature));
        Assert.IsFalse(TlsCertificateVerifier.Verify(ecdsa, 0x0503, content, ecdsaSignature));
        Assert.IsFalse(TlsCertificateVerifier.Verify(wider, 0x0403, content, p384.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
        Assert.IsFalse(TlsCertificateVerifier.Verify(ecdsa, 0x0203, content, ecdsaSignature));

        using RSA rsa = RSA.Create(2048);
        using RSA small = RSA.Create(1024);
        using X509Certificate2 rsaCertificate = SelfSigned(rsa, "CN=localhost");
        using X509Certificate2 smallCertificate = SelfSigned(small, "CN=localhost");
        byte[] pss = rsa.SignData(content, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        Assert.IsTrue(TlsCertificateVerifier.Verify(rsaCertificate, 0x0804, content, pss));
        Assert.IsFalse(TlsCertificateVerifier.Verify(rsaCertificate, 0x0401, content, rsa.SignData(content, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
        Assert.IsFalse(TlsCertificateVerifier.Verify(rsaCertificate, 0x0201, content, pss));
        Assert.IsFalse(TlsCertificateVerifier.Verify(rsaCertificate, 0x0809, content, pss));
        Assert.IsFalse(TlsCertificateVerifier.Verify(smallCertificate, 0x0804, content, small.SignData(content, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    }

    private static SslPolicyErrors CaptureSslStreamErrors(X509Certificate2 certificate, string host)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(() =>
        {
            using TcpClient client = listener.AcceptTcpClient();
            using var stream = new SslStream(client.GetStream());
            stream.AuthenticateAsServer(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
        });
        SslPolicyErrors seen = SslPolicyErrors.None;
        using (var tcp = new TcpClient(IPAddress.Loopback.ToString(), port))
        using (var stream = new SslStream(tcp.GetStream()))
        {
            stream.AuthenticateAsClient(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                {
                    seen = errors;
                    return true;
                },
            });
        }

        server.GetAwaiter().GetResult();
        return seen;
    }

    private static X509Certificate2 Authority(string subject)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using X509Certificate2 created = request.CreateSelfSigned(NotBefore, NotAfter);
        return Persist(created);
    }

    private static X509Certificate2 Issue(X509Certificate2 issuer, string subject, bool ca, string? dns, string? eku, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (dns is not null)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(dns);
            request.CertificateExtensions.Add(names.Build());
        }

        if (eku is not null)
        {
            var oids = new OidCollection { new Oid(eku) };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
        }

        using X509Certificate2 issued = request.Create(issuer, notBefore, notAfter, [1, 2, 3, (byte)subject.Length]);
        using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);
        return Persist(withKey);
    }

    private static X509Certificate2 Persist(X509Certificate2 certificate)
    {
        byte[] pfx = certificate.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 SelfSigned(AsymmetricAlgorithm key, string subject)
    {
        CertificateRequest request = key switch
        {
            ECDsa ecdsa => new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256),
            RSA rsa => new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => throw new ArgumentException("Expected an RSA or ECDSA key.", nameof(key)),
        };
        return request.CreateSelfSigned(NotBefore, NotAfter);
    }

    private static byte[] CertificateList(byte[]? statusRequest, byte[]? timestamps, params X509Certificate2[] certificates)
    {
        var list = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < certificates.Length; i++)
        {
            byte[] der = certificates[i].RawData;
            list.Add((byte)(der.Length >> 16));
            list.Add((byte)(der.Length >> 8));
            list.Add((byte)der.Length);
            list.AddRange(der);
            if (i == 0 && (statusRequest is not null || timestamps is not null))
            {
                var extensions = new System.Collections.Generic.List<byte>();
                AddExtension(extensions, 5, statusRequest);
                AddExtension(extensions, 18, timestamps);
                list.Add((byte)(extensions.Count >> 8));
                list.Add((byte)extensions.Count);
                list.AddRange(extensions);
            }
            else
            {
                list.Add(0);
                list.Add(0);
            }
        }

        return list.ToArray();
    }

    private static void AddExtension(System.Collections.Generic.List<byte> destination, ushort type, byte[]? body)
    {
        if (body is null)
        {
            return;
        }

        destination.Add((byte)(type >> 8));
        destination.Add((byte)type);
        destination.Add((byte)(body.Length >> 8));
        destination.Add((byte)body.Length);
        destination.AddRange(body);
    }

    private static string Status(X509Chain? chain)
    {
        if (chain is null)
        {
            return "no chain";
        }

        var text = new System.Text.StringBuilder();
        foreach (X509ChainStatus status in chain.ChainStatus)
        {
            text.Append(status.Status).Append(' ').Append(status.StatusInformation);
        }

        return text.Length == 0 ? "empty status" : text.ToString();
    }
}
