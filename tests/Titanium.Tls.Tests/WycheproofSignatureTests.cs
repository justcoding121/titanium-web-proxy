using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Security;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class WycheproofSignatureTests
{
    [TestMethod]
    public void EcdsaSha256MatchesWycheproof()
    {
        AssertVerified("ecdsa_secp256r1_sha256_test.json", 0x0403);
    }

    [TestMethod]
    public void EcdsaSha384MatchesWycheproof()
    {
        AssertVerified("ecdsa_secp384r1_sha384_test.json", 0x0503);
    }

    [TestMethod]
    public void RsaPssSha256MatchesWycheproof()
    {
        AssertVerified("rsa_pss_2048_sha256_mgf1_32_test.json", 0x0804);
    }

    private static void AssertVerified(string file, ushort scheme)
    {
        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(WycheproofX25519Tests.FindFixture(file)));
        AsymmetricKeyParameter signer = SigningKey();
        int valid = 0;
        foreach (JsonElement group in document.RootElement.GetProperty("testGroups").EnumerateArray())
        {
            byte[] spki = Convert.FromHexString(group.GetProperty("publicKeyDer").GetString()!);
            using X509Certificate2 certificate = CertificateFor(spki, signer);
            foreach (JsonElement test in group.GetProperty("tests").EnumerateArray())
            {
                string result = test.GetProperty("result").GetString()!;
                if (result == "acceptable" || PlatformAccepts(test))
                {
                    continue;
                }

                byte[] message = Convert.FromHexString(test.GetProperty("msg").GetString()!);
                byte[] signature = Convert.FromHexString(test.GetProperty("sig").GetString()!);
                bool verified = TlsCertificateVerifier.Verify(certificate, scheme, message, signature);
                int id = test.GetProperty("tcId").GetInt32();
                if (result == "valid")
                {
                    Assert.IsTrue(verified, file + " tcId " + id);
                    valid++;
                }
                else
                {
                    Assert.IsFalse(verified, file + " tcId " + id);
                }
            }
        }

        Assert.IsTrue(valid > 0, file);
    }

    private static bool PlatformAccepts(JsonElement test)
    {
        if (!test.TryGetProperty("flags", out JsonElement flags))
        {
            return false;
        }

        foreach (JsonElement flag in flags.EnumerateArray())
        {
            string? name = flag.GetString();
            if (name is "MissingZero" or "BerEncodedSignature")
            {
                return true;
            }
        }

        return false;
    }

    private static AsymmetricKeyParameter SigningKey()
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        return generator.GenerateKeyPair().Private;
    }

    private static X509Certificate2 CertificateFor(byte[] subjectPublicKeyInfo, AsymmetricKeyParameter signer)
    {
        var generator = new Org.BouncyCastle.X509.X509V3CertificateGenerator();
        var name = new X509Name("CN=wycheproof");
        generator.SetSerialNumber(Org.BouncyCastle.Math.BigInteger.One);
        generator.SetIssuerDN(name);
        generator.SetSubjectDN(name);
        generator.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        generator.SetNotAfter(DateTime.UtcNow.AddYears(1));
        generator.SetPublicKey(PublicKeyFactory.CreateKey(subjectPublicKeyInfo));
        Org.BouncyCastle.X509.X509Certificate certificate = generator.Generate(new Asn1SignatureFactory("SHA256WITHRSA", signer));
        return X509CertificateLoader.LoadCertificate(certificate.GetEncoded());
    }
}
