using System;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class HkdfTests
{
    [TestMethod]
    public void ExpandLabelMatchesRfc8448ClientHandshakeTraffic()
    {
        byte[] secret = Hex("1dc826e93606aa6fdc0aadc12f741b01046aa6b99f691ed221a9f0ca043fbeac");
        byte[] transcript = Hex("860c06edc07858ee8e78f0e7428c58edd6b43f2ca3e6e95f02ed063cf0e1cad8");
        byte[] expected = Hex("b3eddb126e067f35a780b3abf45e2d8f3b1a950738f52e9600746a0e27a55a21");
        Span<byte> output = stackalloc byte[32];
        TlsHkdf.ExpandLabel(HashAlgorithmName.SHA256, secret, "c hs traffic"u8, transcript, output);
        Assert.IsTrue(output.SequenceEqual(expected));
    }

    [TestMethod]
    public void ExpandLabelMatchesRfc8448ServerHandshakeTraffic()
    {
        byte[] secret = Hex("1dc826e93606aa6fdc0aadc12f741b01046aa6b99f691ed221a9f0ca043fbeac");
        byte[] transcript = Hex("860c06edc07858ee8e78f0e7428c58edd6b43f2ca3e6e95f02ed063cf0e1cad8");
        byte[] expected = Hex("b67b7d690cc16c4e75e54213cb2d37b4e9c912bcded9105d42befd59d391ad38");
        Span<byte> output = stackalloc byte[32];
        TlsHkdf.ExpandLabel(HashAlgorithmName.SHA256, secret, "s hs traffic"u8, transcript, output);
        Assert.IsTrue(output.SequenceEqual(expected));
    }

    [TestMethod]
    public void DeriveSecretMatchesRfc8448DerivedFromEarly()
    {
        byte[] secret = Hex("33ad0a1c607ec03b09e6cd9893680ce210adf300aa1f2660e1b22e10f170f92a");
        byte[] emptyHash = Hex("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        byte[] expected = Hex("6f2615a108c702c5678f54fc9dbab69716c076189c48250cebeac3576c3611ba");
        Span<byte> output = stackalloc byte[32];
        TlsHkdf.DeriveSecret(HashAlgorithmName.SHA256, secret, "derived"u8, emptyHash, output);
        Assert.IsTrue(output.SequenceEqual(expected));
    }

    [TestMethod]
    public void TranscriptHashesAppendedMessagesAndReplacesClientHelloWithMessageHash()
    {
        byte[] hello = [1, 0, 0, 2, 0xAA, 0xBB];
        using var transcript = new TranscriptStore(HashAlgorithmName.SHA256);
        transcript.Append(hello);
        Span<byte> hash = stackalloc byte[32];
        transcript.CurrentHash(hash);
        Span<byte> direct = stackalloc byte[32];
        SHA256.HashData(hello, direct);
        Assert.IsTrue(hash.SequenceEqual(direct));

        Span<byte> withExtra = stackalloc byte[32];
        byte[] extra = [9, 9];
        transcript.HashWith(extra, withExtra);
        byte[] combined = [1, 0, 0, 2, 0xAA, 0xBB, 9, 9];
        SHA256.HashData(combined, direct);
        Assert.IsTrue(withExtra.SequenceEqual(direct));
        Assert.AreEqual(hello.Length, transcript.Length);

        transcript.ReplaceWithMessageHash();
        Assert.AreEqual(36, transcript.Length);
        Assert.AreEqual(254, transcript.Bytes[0]);
        Assert.AreEqual(32, transcript.Bytes[3]);
        Assert.IsTrue(transcript.Bytes.Slice(4).SequenceEqual(hash));
    }

    [TestMethod]
    public void BclHkdfMatchesRfc5869Case1()
    {
        AssertRfc5869(
            "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b",
            "000102030405060708090a0b0c",
            "f0f1f2f3f4f5f6f7f8f9",
            "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5",
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");
    }

    [TestMethod]
    public void BclHkdfMatchesRfc5869Case2()
    {
        AssertRfc5869(
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f",
            "606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa0a1a2a3a4a5a6a7a8a9aaabacadaeaf",
            "b0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7e8e9eaebecedeeeff0f1f2f3f4f5f6f7f8f9fafbfcfdfeff",
            "06a6b88c5853361a06104c9ceb35b45cef760014904671014a193f40c15fc244",
            "b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71cc30c58179ec3e87c14c01d5c1f3434f1d87");
    }

    [TestMethod]
    public void BclHkdfMatchesRfc5869Case3()
    {
        AssertRfc5869(
            "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b",
            "",
            "",
            "19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04",
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8");
    }

    [TestMethod]
    public void ExpandLabelAcceptsTheMaximumLabelAndRejectsOverflow()
    {
        byte[] secret = new byte[32];
        byte[] output = new byte[32];
        TlsHkdf.ExpandLabel(HashAlgorithmName.SHA256, secret, new byte[249], ReadOnlySpan<byte>.Empty, output);
        Assert.ThrowsException<ArgumentException>(() =>
            TlsHkdf.ExpandLabel(HashAlgorithmName.SHA256, secret, new byte[250], ReadOnlySpan<byte>.Empty, output));
        Assert.ThrowsException<ArgumentException>(() =>
            TlsHkdf.ExpandLabel(HashAlgorithmName.SHA256, secret, "x"u8, new byte[256], output));
    }

    [TestMethod]
    public void ExpandLabelAddsNoAllocationBeyondHkdfExpand()
    {
        byte[] secret = new byte[32];
        byte[] context = new byte[32];
        Span<byte> output = stackalloc byte[32];
        Span<byte> info = stackalloc byte[54];
        info.Fill(1);
        HKDF.Expand(HashAlgorithmName.SHA256, secret, output, info);
        TlsHkdf.ExpandLabel(HashAlgorithmName.SHA256, secret, "c hs traffic"u8, context, output);

        const int iterations = 64;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            HKDF.Expand(HashAlgorithmName.SHA256, secret, output, info);
        }

        long expandOnly = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            TlsHkdf.ExpandLabel(HashAlgorithmName.SHA256, secret, "c hs traffic"u8, context, output);
        }

        long expandLabel = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(expandLabel <= expandOnly);
    }

    private static void AssertRfc5869(string ikmHex, string saltHex, string infoHex, string prkHex, string okmHex)
    {
        byte[] ikm = Hex(ikmHex);
        byte[] salt = Hex(saltHex);
        byte[] info = Hex(infoHex);
        byte[] expectedPrk = Hex(prkHex);
        byte[] expectedOkm = Hex(okmHex);
        byte[] prk = new byte[32];
        TlsHkdf.Extract(HashAlgorithmName.SHA256, ikm, salt, prk);
        Assert.IsTrue(prk.AsSpan().SequenceEqual(expectedPrk));
        byte[] okm = new byte[expectedOkm.Length];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, okm, info);
        Assert.IsTrue(okm.AsSpan().SequenceEqual(expectedOkm));
    }

    private static byte[] Hex(string text)
    {
        byte[] bytes = new byte[text.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
        }

        return bytes;
    }
}
