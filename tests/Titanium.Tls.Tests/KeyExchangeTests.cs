using System;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Math;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class KeyExchangeTests
{
    [TestMethod]
    public void X25519ShareMatchesTheRfc7748AlicePublicKey()
    {
        IKeyExchangeGroup group = KeyExchangeGroups.Create(NamedGroup.X25519);
        using IPrivateKeyShare share = group.GenerateKeyShare(new FixedEntropy(Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")));
        Span<byte> publicKey = stackalloc byte[32];
        share.WritePublicKey(publicKey);
        Assert.IsTrue(publicKey.SequenceEqual(Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a")));
    }

    [TestMethod]
    public void BothSidesAgreeForEachFiniteGroup()
    {
        Agree(NamedGroup.X25519);
        Agree(NamedGroup.Secp256r1);
        Agree(NamedGroup.Secp384r1);
    }

    [TestMethod]
    public void InvalidPeerShareIsRejected()
    {
        IKeyExchangeGroup group = KeyExchangeGroups.Create(NamedGroup.X25519);
        using IPrivateKeyShare share = group.GenerateKeyShare(new SystemEntropy());
        Span<byte> secret = stackalloc byte[32];
        Assert.IsFalse(share.TryComputeSharedSecret(new byte[31], secret, out _));
        Assert.IsFalse(share.TryComputeSharedSecret(new byte[32], secret, out _));

        IKeyExchangeGroup nist = KeyExchangeGroups.Create(NamedGroup.Secp256r1);
        using IPrivateKeyShare nistShare = nist.GenerateKeyShare(new SystemEntropy());
        byte[] offCurve = new byte[65];
        offCurve[0] = 0x04;
        offCurve[1] = 1;
        Span<byte> nistSecret = stackalloc byte[32];
        Assert.IsFalse(nistShare.TryComputeSharedSecret(offCurve, nistSecret, out _));
        offCurve[0] = 0x02;
        Assert.IsFalse(nistShare.TryComputeSharedSecret(offCurve, nistSecret, out _));
    }

    [TestMethod]
    public void DisposedShareRejectsFurtherUse()
    {
        IKeyExchangeGroup group = KeyExchangeGroups.Create(NamedGroup.X25519);
        IPrivateKeyShare share = group.GenerateKeyShare(new SystemEntropy());
        share.Dispose();
        byte[] publicKey = new byte[32];
        Assert.ThrowsExactly<ObjectDisposedException>(() => share.WritePublicKey(publicKey));
    }

    [TestMethod]
    public void NistSharesMatchWycheproofEcPoints()
    {
        MatchWycheproof(NamedGroup.Secp256r1, "ecdh_secp256r1_ecpoint_test.json", "secp256r1");
        MatchWycheproof(NamedGroup.Secp384r1, "ecdh_secp384r1_ecpoint_test.json", "secp384r1");
    }

    private static void Agree(NamedGroup name)
    {
        IKeyExchangeGroup group = KeyExchangeGroups.Create(name);
        using IPrivateKeyShare alice = group.GenerateKeyShare(new SystemEntropy());
        using IPrivateKeyShare bob = group.GenerateKeyShare(new SystemEntropy());
        byte[] alicePublic = new byte[group.PublicKeyLength];
        byte[] bobPublic = new byte[group.PublicKeyLength];
        alice.WritePublicKey(alicePublic);
        bob.WritePublicKey(bobPublic);
        byte[] aliceSecret = new byte[group.SharedSecretLength];
        byte[] bobSecret = new byte[group.SharedSecretLength];
        Assert.IsTrue(alice.TryComputeSharedSecret(bobPublic, aliceSecret, out int aliceWritten));
        Assert.IsTrue(bob.TryComputeSharedSecret(alicePublic, bobSecret, out int bobWritten));
        Assert.AreEqual(group.SharedSecretLength, aliceWritten);
        Assert.AreEqual(group.SharedSecretLength, bobWritten);
        Assert.IsTrue(aliceSecret.AsSpan().SequenceEqual(bobSecret));
    }

    private static void MatchWycheproof(NamedGroup name, string file, string curveName)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(WycheproofX25519Tests.FindFixture(file)));
        X9ECParameters parameters = SecNamedCurves.GetByName(curveName);
        NistKeyExchange group = (NistKeyExchange)KeyExchangeGroups.Create(name);
        int valid = 0;
        byte[] ignored = new byte[group.SharedSecretLength];
        foreach (JsonElement testGroup in document.RootElement.GetProperty("testGroups").EnumerateArray())
        {
            foreach (JsonElement test in testGroup.GetProperty("tests").EnumerateArray())
            {
                byte[] peer = Convert.FromHexString(test.GetProperty("public").GetString()!);
                string result = test.GetProperty("result").GetString()!;
                if (result != "valid")
                {
                    using IPrivateKeyShare probe = group.GenerateKeyShare(new SystemEntropy());
                    Assert.IsFalse(probe.TryComputeSharedSecret(peer, ignored, out _), "tcId " + test.GetProperty("tcId").GetInt32());
                    continue;
                }

                byte[] scalar = LeftPad(Convert.FromHexString(test.GetProperty("private").GetString()!), group.SharedSecretLength);
                byte[] ownPublic = UncompressedPublic(parameters, scalar);
                using IPrivateKeyShare share = group.Import(scalar, ownPublic);
                byte[] secret = new byte[group.SharedSecretLength];
                Assert.IsTrue(share.TryComputeSharedSecret(peer, secret, out _), "tcId " + test.GetProperty("tcId").GetInt32());
                Assert.IsTrue(secret.AsSpan().SequenceEqual(Convert.FromHexString(test.GetProperty("shared").GetString()!)), "tcId " + test.GetProperty("tcId").GetInt32());
                valid++;
            }
        }

        Assert.IsTrue(valid > 0);
    }

    private static byte[] UncompressedPublic(X9ECParameters parameters, byte[] scalar)
    {
        return parameters.G.Multiply(new BigInteger(1, scalar)).Normalize().GetEncoded(compressed: false);
    }

    private static byte[] LeftPad(byte[] value, int length)
    {
        if (value.Length == length)
        {
            return value;
        }

        byte[] padded = new byte[length];
        if (value.Length > length)
        {
            value.AsSpan(value.Length - length).CopyTo(padded);
            return padded;
        }

        value.CopyTo(padded.AsSpan(length - value.Length));
        return padded;
    }

    private static byte[] Hex(string text)
    {
        return Convert.FromHexString(text);
    }
}
