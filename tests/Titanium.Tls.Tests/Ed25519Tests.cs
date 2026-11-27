using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class Ed25519Tests
{
    [TestMethod]
    public void Rfc8032Section71Verifies()
    {
        AssertVerified("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "", "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        AssertVerified("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72", "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        AssertVerified("fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82", "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a");
        string[] vector = File.ReadAllLines(FindFixture("rfc8032-ed25519-1024.txt"));
        Assert.IsTrue(Ed25519Verifier.Verify(
            Convert.FromHexString(vector[0]["PUBLIC ".Length..]),
            Convert.FromHexString(vector[1]["MESSAGE ".Length..]),
            Convert.FromHexString(vector[2]["SIGNATURE ".Length..])));
    }

    [TestMethod]
    public void RejectsLengthsCanonicalSSmallOrderAndFips()
    {
        byte[] publicKey = Convert.FromHexString("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        byte[] signature = Convert.FromHexString("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        byte[] message = Convert.FromHexString("72");
        Assert.IsFalse(Ed25519Verifier.Verify(publicKey, message, signature[..63]));
        Assert.IsFalse(Ed25519Verifier.Verify(publicKey[..31], message, signature));
        signature[0] ^= 0x01;
        Assert.IsFalse(Ed25519Verifier.Verify(publicKey, message, signature));
        signature[0] ^= 0x01;
        BigInteger order = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493", CultureInfo.InvariantCulture);
        BigInteger s = new(signature.AsSpan(32), isUnsigned: true, isBigEndian: false);
        signature.AsSpan(32).Clear();
        Assert.IsTrue((s + order).TryWriteBytes(signature.AsSpan(32), out _, isUnsigned: true, isBigEndian: false));
        Assert.IsFalse(Ed25519Verifier.Verify(publicKey, message, signature));
        byte[] identity = new byte[32];
        identity[0] = 1;
        Assert.IsFalse(Ed25519Verifier.Verify(identity, message, signature));
        signature = Convert.FromHexString("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        Assert.IsFalse(Ed25519Verifier.Verify(publicKey, message, signature, TlsAlgorithmPolicy.FipsApprovedOnly));
        Assert.IsTrue(Ed25519Verifier.VerifyScheme(SignatureScheme.Ed25519, publicKey, message, signature));
        Assert.IsFalse(Ed25519Verifier.VerifyScheme((SignatureScheme)0, publicKey, message, signature));
    }

    private static void AssertVerified(string publicKey, string message, string signature)
    {
        Assert.IsTrue(Ed25519Verifier.Verify(
            Convert.FromHexString(publicKey),
            Convert.FromHexString(message),
            Convert.FromHexString(signature)));
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
