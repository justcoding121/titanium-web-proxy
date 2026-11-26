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
