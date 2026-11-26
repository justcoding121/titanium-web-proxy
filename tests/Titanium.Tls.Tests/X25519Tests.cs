using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class X25519Tests
{
    [TestMethod]
    public void ScalarMultMatchesRfc7748Section52()
    {
        byte[] scalar = Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4");
        byte[] u = Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c");
        Span<byte> result = stackalloc byte[32];
        X25519.ScalarMult(scalar, u, result);
        Assert.IsTrue(result.SequenceEqual(Hex("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552")));
    }

    [TestMethod]
    public void AliceAndBobAgreeOnRfc7748Section61()
    {
        byte[] alicePrivate = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        byte[] bobPrivate = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        Span<byte> alicePublic = stackalloc byte[32];
        Span<byte> bobPublic = stackalloc byte[32];
        X25519.PublicKey(alicePrivate, alicePublic);
        X25519.PublicKey(bobPrivate, bobPublic);
        Assert.IsTrue(alicePublic.SequenceEqual(Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a")));
        Assert.IsTrue(bobPublic.SequenceEqual(Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f")));

        Span<byte> aliceShared = stackalloc byte[32];
        Span<byte> bobShared = stackalloc byte[32];
        Assert.IsTrue(X25519.TryAgree(alicePrivate, bobPublic, aliceShared));
        Assert.IsTrue(X25519.TryAgree(bobPrivate, alicePublic, bobShared));
        Assert.IsTrue(aliceShared.SequenceEqual(Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742")));
        Assert.IsTrue(bobShared.SequenceEqual(aliceShared));
    }

    [TestMethod]
    public void IteratedScalarMultMatchesRfc7748AfterOneThousand()
    {
        byte[] scalar = new byte[32];
        byte[] u = new byte[32];
        scalar[0] = 9;
        u[0] = 9;
        Span<byte> result = stackalloc byte[32];
        Span<byte> previousScalar = stackalloc byte[32];
        for (int i = 0; i < 1000; i++)
        {
            scalar.AsSpan().CopyTo(previousScalar);
            X25519.ScalarMult(scalar, u, result);
            result.CopyTo(scalar);
            previousScalar.CopyTo(u);
            if (i == 0)
            {
                Assert.IsTrue(scalar.AsSpan().SequenceEqual(Hex("422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079")));
            }
        }

        Assert.IsTrue(scalar.AsSpan().SequenceEqual(Hex("684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51")));
    }

    [TestMethod]
    public void LowOrderPublicKeyIsRejected()
    {
        Span<byte> shared = stackalloc byte[32];
        shared.Fill(1);
        Assert.IsFalse(X25519.TryAgree(new byte[32], new byte[32], shared));
        Assert.IsTrue(shared.SequenceEqual(new byte[32]));
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
