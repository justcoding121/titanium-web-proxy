using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class Rfc8448CertificateTests
{
    private const string ClientHello = "010000c00303cb34ecb1e78163ba1c38c6dacb196a6dffa21a8d9912ec18a2ef6283024dece7000006130113031302010000910000000b0009000006736572766572ff01000100000a00140012001d0017001800190100010101020103010400230000003300260024001d002099381de560e4bd43d23d8e435a7dbafeb3c06e51c13cae4d5413691e529aaf2c002b0003020304000d0020001e040305030603020308040805080604010501060102010402050206020202002d00020101001c00024001";
    private const string ServerHello = "020000560303a6af06a4121860dc5e6e60249cd34c95930c8ac5cb1434dac155772ed3e2692800130100002e00330024001d0020c9828876112095fe66762bdbf7c672e156d6cc253b833df1dd69b1b04e751f0f002b00020304";

    [TestMethod]
    public void Section3CertificateVerifyAndFinishedMatch()
    {
        byte[] flight = Convert.FromHexString(File.ReadAllText(FindFixture("rfc8448-server-handshake.hex")).Trim());
        int at = 0;
        ReadOnlySpan<byte> extensions = Take(flight, ref at);
        ReadOnlySpan<byte> certificate = Take(flight, ref at);
        ReadOnlySpan<byte> verify = Take(flight, ref at);
        ReadOnlySpan<byte> finished = Take(flight, ref at);
        Assert.AreEqual(0, flight.Length - at);
        Assert.IsTrue(HandshakeMessages.TryReadCertificateVerify(verify, out ushort scheme, out ReadOnlySpan<byte> signature, out _));
        Assert.AreEqual(0x0804, scheme);

        using var transcript = new TranscriptStore(HashAlgorithmName.SHA256);
        transcript.Append(Convert.FromHexString(ClientHello));
        transcript.Append(Convert.FromHexString(ServerHello));
        transcript.Append(extensions);
        transcript.Append(certificate);
        Span<byte> hash = stackalloc byte[32];
        transcript.CurrentHash(hash);
        int derAt = 4 + 1 + 3 + 3;
        int derLength = (certificate[derAt - 3] << 16) | (certificate[derAt - 2] << 8) | certificate[derAt - 1];
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.Slice(derAt, derLength));
        Span<byte> content = stackalloc byte[64 + 33 + 1 + 32];
        content[..64].Fill(0x20);
        "TLS 1.3, server CertificateVerify"u8.CopyTo(content[64..]);
        content[64 + 33] = 0;
        hash.CopyTo(content[(64 + 33 + 1)..]);
        Assert.AreEqual("764D6632B3C35C3F3205E3499AC3EDBAABB88295FBA751461D3678E2E5EA0687", Convert.ToHexString(hash));
        using RSA publicKey = leaf.GetRSAPublicKey()!;
        Assert.IsTrue(publicKey.VerifyData(content, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.IsTrue(publicKey.KeySize < 2048);
        Assert.IsFalse(TlsCertificateVerifier.Verify(leaf, scheme, content, signature));

        transcript.Append(verify);
        transcript.CurrentHash(hash);
        Span<byte> handshakeSecret = stackalloc byte[32];
        Convert.FromHexString("b67b7d690cc16c4e75e54213cb2d37b4e9c912bcded9105d42befd59d391ad38").CopyTo(handshakeSecret);
        Span<byte> finishedKey = stackalloc byte[32];
        TlsKeySchedule.FinishedKey(HashAlgorithmName.SHA256, handshakeSecret, finishedKey);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(finishedKey, hash, mac);
        Assert.IsTrue(mac.SequenceEqual(finished[4..]));
    }

    private static ReadOnlySpan<byte> Take(byte[] flight, ref int at)
    {
        int length = (flight[at + 1] << 16) | (flight[at + 2] << 8) | flight[at + 3];
        int total = 4 + length;
        ReadOnlySpan<byte> message = flight.AsSpan(at, total);
        at += total;
        return message;
    }

    private static string FindFixture(string name)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "Titanium.Tls.Tests", "Fixtures", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        Assert.Fail(name + " was not found.");
        return string.Empty;
    }
}
