using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.StreamExtended.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class SslExtensionBufferCoverageTests
{
    [TestMethod]
    public void SupportedVersions_MapsKnownGreaseAndDrafts()
    {
        // length-prefixed client list: Ssl3, Tls1.0-1.3, grease 0x0A0A, draft 0x7F17
        var payload = new byte[]
        {
            14,
            0x03, 0x00,
            0x03, 0x01,
            0x03, 0x02,
            0x03, 0x03,
            0x03, 0x04,
            0x0A, 0x0A,
            0x7F, 0x17
        };
        var data = new SslExtension(43, payload, 0).Data;
        StringAssert.Contains(data, "Ssl3.0");
        StringAssert.Contains(data, "Tls1.0");
        StringAssert.Contains(data, "Tls1.1");
        StringAssert.Contains(data, "Tls1.2");
        StringAssert.Contains(data, "Tls1.3");
        StringAssert.Contains(data, "grease");
        StringAssert.Contains(data, "Tls1.3_draft23");
    }

    [TestMethod]
    public void SignatureAlgorithms_LegacyMatrix_HitsAllHashAndPubkeyBranches()
    {
        // Legacy path switches on low byte (pubkey) then high byte (hash).
        // Use [hash, pubkey] pairs that avoid named SignatureScheme ids.
        byte[] pairs =
        [
            0x00, 0x00, // anonymous_none
            0x01, 0x01, // rsa_md5
            0x02, 0x02, // dsa_sha1
            0x03, 0x03, // ecdsa_sha224
            0x04, 0x07, // ed25519_sha256
            0x05, 0x08, // ed448_sha384
            0x06, 0x40, // gost256_sha512
            0x08, 0x41, // gost512_Intrinsic
            0x09, 0x09, // Reserved_Reserved
            0xE0, 0xE0 // Private Use / Private Use
        ];
        var payload = new byte[2 + pairs.Length];
        payload[0] = (byte)(pairs.Length >> 8);
        payload[1] = (byte)pairs.Length;
        Buffer.BlockCopy(pairs, 0, payload, 2, pairs.Length);

        var data = new SslExtension(13, payload, 0).Data;
        StringAssert.Contains(data, "anonymous_none");
        StringAssert.Contains(data, "rsa_md5");
        StringAssert.Contains(data, "dsa_sha1");
        StringAssert.Contains(data, "ecdsa_sha224");
        StringAssert.Contains(data, "ed25519_sha256");
        StringAssert.Contains(data, "ed448_sha384");
        StringAssert.Contains(data, "gostr34102012_256_sha512");
        StringAssert.Contains(data, "gostr34102012_512_Intrinsic");
        StringAssert.Contains(data, "Reserved for Private Use");
    }

    [TestMethod]
    public void ServerName_SkipsNonHostNameEntries()
    {
        // Parser starts at offset 2: entries are type(1)+len(2)+data. Type!=0 is skipped.
        var host = Encoding.ASCII.GetBytes("a.example");
        var other = Encoding.ASCII.GetBytes("x");
        var payload = new byte[2 + (3 + other.Length) + (3 + host.Length)];
        payload[0] = (byte)((payload.Length - 2) >> 8);
        payload[1] = (byte)(payload.Length - 2);
        var i = 2;
        payload[i++] = 1; // non host_name
        payload[i++] = (byte)(other.Length >> 8);
        payload[i++] = (byte)other.Length;
        Buffer.BlockCopy(other, 0, payload, i, other.Length);
        i += other.Length;
        payload[i++] = 0; // host_name
        payload[i++] = (byte)(host.Length >> 8);
        payload[i++] = (byte)host.Length;
        Buffer.BlockCopy(host, 0, payload, i, host.Length);

        var data = new SslExtension(0, payload, 0).Data;
        Assert.AreEqual("a.example", data);
    }

    [TestMethod]
    public void StatusRequest_OcspAndPadding_AndEmptyGroups()
    {
        Assert.AreEqual("OCSP - Implicit Responder",
            new SslExtension(5, new byte[] { 1, 0, 0, 0, 0 }, 0).Data);

        var padding = new SslExtension(21, new byte[12], 0).Data;
        StringAssert.Contains(padding, "null bytes");

        Assert.AreEqual(string.Empty, new SslExtension(10, new byte[] { 0, 0 }, 0).Data);
        Assert.AreEqual(string.Empty, new SslExtension(11, Array.Empty<byte>(), 0).Data);
    }

    [TestMethod]
    public void Name_CoversRemainingIanaIds()
    {
        int[] ids =
        [
            2, 3, 4, 6, 7, 8, 9, 12, 14, 15, 17, 18, 19, 20, 22, 23, 24, 25, 26,
            41, 42, 44, 46, 48, 30031, 35655
        ];
        foreach (var id in ids)
        {
            var name = new SslExtension(id, Array.Empty<byte>(), 0).Name;
            Assert.IsFalse(string.IsNullOrWhiteSpace(name), $"id {id}");
            Assert.IsFalse(name.StartsWith("unknown_", StringComparison.Ordinal), $"id {id} -> {name}");
        }

        Assert.AreEqual("unknown_ff", new SslExtension(0xFF, Array.Empty<byte>(), 0).Name);
    }

    [TestMethod]
    public void SignatureAlgorithms_NamedCaseHits_AllModernIds()
    {
        ushort[] algs =
        [
            0x0401, 0x0501, 0x0601, 0x0403, 0x0503, 0x0603,
            0x0804, 0x0805, 0x0806, 0x0807, 0x0808, 0x0809, 0x080A, 0x080B,
            0x0201, 0x0203
        ];
        var body = algs.SelectMany(a => new[] { (byte)(a >> 8), (byte)a }).ToArray();
        var payload = new byte[2 + body.Length];
        payload[0] = (byte)(body.Length >> 8);
        payload[1] = (byte)body.Length;
        Buffer.BlockCopy(body, 0, payload, 2, body.Length);
        var data = new SslExtension(50, payload, 0).Data; // type 50 also uses GetSignatureAlgorithms
        StringAssert.Contains(data, "rsa_pkcs1_sha256");
        StringAssert.Contains(data, "ecdsa_secp256r1_sha256");
        StringAssert.Contains(data, "rsa_pss_rsae_sha256");
        StringAssert.Contains(data, "ed25519");
        StringAssert.Contains(data, "rsa_pss_pss_sha512");
        StringAssert.Contains(data, "rsa_pkcs1_sha1");
    }
}
