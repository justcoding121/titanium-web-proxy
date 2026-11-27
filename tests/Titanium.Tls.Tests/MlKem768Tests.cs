using System;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class KeccakTests
{
    [TestMethod]
    public void ManagedSha3AndShakeMatchThePlatform()
    {
        if (!SHA3_256.IsSupported)
        {
            Assert.Inconclusive("SHA-3 is not available on this runtime.");
            return;
        }

        byte[] message = "ML-KEM"u8.ToArray();
        Span<byte> managed = stackalloc byte[64];
        Span<byte> platform = stackalloc byte[64];
        IKeccak keccak = ManagedKeccak.Instance;
        keccak.Sha3_256(message, managed[..32]);
        SHA3_256.HashData(message, platform[..32]);
        Assert.IsTrue(managed[..32].SequenceEqual(platform[..32]));
        keccak.Sha3_512(message, managed);
        SHA3_512.HashData(message, platform);
        Assert.IsTrue(managed.SequenceEqual(platform));
        keccak.Shake128(message, managed);
        Shake128.HashData(message, platform);
        Assert.IsTrue(managed.SequenceEqual(platform));
        keccak.Shake256(message, managed);
        Shake256.HashData(message, platform);
        Assert.IsTrue(managed.SequenceEqual(platform));
        keccak.Sha3_256(ReadOnlySpan<byte>.Empty, managed[..32]);
        SHA3_256.HashData(ReadOnlySpan<byte>.Empty, platform[..32]);
        Assert.IsTrue(managed[..32].SequenceEqual(platform[..32]));
    }
}

[TestClass]
public sealed class MlKem768Tests
{
    [TestMethod]
    public void ZetaPowersMatchFips203AppendixA()
    {
        Assert.AreEqual(1, Pow(17, 0));
        Assert.AreEqual(1729, Pow(17, BitRev7(1)));
        Assert.AreEqual(2580, Pow(17, BitRev7(2)));
        Assert.AreEqual(3289, Pow(17, BitRev7(3)));
        Assert.AreEqual(17, Pow(17, (2 * BitRev7(0)) + 1));
        Assert.AreEqual(3329 - 17, Pow(17, (2 * BitRev7(1)) + 1));
        Assert.AreEqual(1, (3303 * 128) % 3329);
    }

    [TestMethod]
    public void ManagedRoundTripAgreesWithItself()
    {
        IMlKem768 kem = ManagedMlKem768.Instance;
        byte[] d = new byte[32];
        byte[] z = new byte[32];
        d[0] = 1;
        z[0] = 2;
        byte[] ek = new byte[ManagedMlKem768.EncapsulationKeyLength];
        byte[] dk = new byte[ManagedMlKem768.DecapsulationKeyLength];
        kem.GenerateKeyPair(ek, dk, d, z);
        byte[] message = new byte[32];
        message[0] = 3;
        byte[] ciphertext = new byte[ManagedMlKem768.CiphertextLength];
        byte[] secret = new byte[32];
        Assert.IsTrue(kem.TryEncapsulate(ek, message, ciphertext, secret));
        byte[] opened = new byte[32];
        Assert.IsTrue(kem.TryDecapsulate(dk, ciphertext, opened));
        Assert.IsTrue(opened.AsSpan().SequenceEqual(secret));
        ciphertext[0] ^= 0xFF;
        byte[] rejected = new byte[32];
        Assert.IsTrue(kem.TryDecapsulate(dk, ciphertext, rejected));
        Assert.IsFalse(rejected.AsSpan().SequenceEqual(secret));
    }

    [TestMethod]
    public void ManagedAgreesWithThePlatformInBothDirections()
    {
        if (!MLKem.IsSupported)
        {
            Assert.Inconclusive("ML-KEM is not available on this runtime.");
            return;
        }

        IMlKem768 managed = ManagedMlKem768.Instance;
        byte[] ek = new byte[ManagedMlKem768.EncapsulationKeyLength];
        byte[] dk = new byte[ManagedMlKem768.DecapsulationKeyLength];
        managed.GenerateKeyPair(ek, dk);
        using MLKem platformKey = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, ek);
        byte[] ciphertext = new byte[ManagedMlKem768.CiphertextLength];
        byte[] platformSecret = new byte[32];
        platformKey.Encapsulate(ciphertext, platformSecret);
        byte[] managedSecret = new byte[32];
        Assert.IsTrue(managed.TryDecapsulate(dk, ciphertext, managedSecret));
        Assert.IsTrue(managedSecret.AsSpan().SequenceEqual(platformSecret));

        using MLKem generated = MLKem.GenerateKey(MLKemAlgorithm.MLKem768);
        generated.ExportEncapsulationKey(ek);
        generated.ExportDecapsulationKey(dk);
        byte[] message = new byte[32];
        RandomNumberGenerator.Fill(message);
        Assert.IsTrue(managed.TryEncapsulate(ek, message, ciphertext, managedSecret));
        generated.Decapsulate(ciphertext, platformSecret);
        Assert.IsTrue(platformSecret.AsSpan().SequenceEqual(managedSecret));
        ciphertext[10] ^= 0x5A;
        Assert.IsTrue(managed.TryDecapsulate(dk, ciphertext, managedSecret));
        generated.Decapsulate(ciphertext, platformSecret);
        Assert.IsTrue(platformSecret.AsSpan().SequenceEqual(managedSecret));
    }

    [TestMethod]
    public void SeededEncapsulationAllocatesNothingAfterWarmup()
    {
        IMlKem768 kem = ManagedMlKem768.Instance;
        byte[] ek = new byte[ManagedMlKem768.EncapsulationKeyLength];
        byte[] dk = new byte[ManagedMlKem768.DecapsulationKeyLength];
        kem.GenerateKeyPair(ek, dk);
        byte[] message = new byte[32];
        byte[] ciphertext = new byte[ManagedMlKem768.CiphertextLength];
        byte[] secret = new byte[32];
        AllocationGate.AssertNone(() => Assert.IsTrue(kem.TryEncapsulate(ek, message, ciphertext, secret)));
    }

    [TestMethod]
    public void EncapsulationCostStaysUnderTwoMilliseconds()
    {
        if (!MLKem.IsSupported)
        {
            Assert.Inconclusive("ML-KEM is not available on this runtime.");
            return;
        }

        IMlKem768 managed = ManagedMlKem768.Instance;
        byte[] ek = new byte[ManagedMlKem768.EncapsulationKeyLength];
        byte[] dk = new byte[ManagedMlKem768.DecapsulationKeyLength];
        managed.GenerateKeyPair(ek, dk);
        byte[] message = new byte[32];
        byte[] ciphertext = new byte[ManagedMlKem768.CiphertextLength];
        byte[] secret = new byte[32];
        managed.TryEncapsulate(ek, message, ciphertext, secret);
        var watch = Stopwatch.StartNew();
        const int iterations = 50;
        for (int i = 0; i < iterations; i++)
        {
            managed.TryEncapsulate(ek, message, ciphertext, secret);
        }

        watch.Stop();
        double managedUs = watch.Elapsed.TotalMicroseconds / iterations;
        using MLKem platform = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, ek);
        platform.Encapsulate(ciphertext, secret);
        watch.Restart();
        for (int i = 0; i < iterations; i++)
        {
            platform.Encapsulate(ciphertext, secret);
        }

        watch.Stop();
        double platformUs = watch.Elapsed.TotalMicroseconds / iterations;
        Assert.IsTrue(managedUs < 2000, $"managed {managedUs:F1} µs, platform {platformUs:F1} µs");
    }

    [TestMethod]
    public void MalformedKeysAreRejected()
    {
        IMlKem768 kem = ManagedMlKem768.Instance;
        byte[] ek = new byte[ManagedMlKem768.EncapsulationKeyLength];
        byte[] dk = new byte[ManagedMlKem768.DecapsulationKeyLength];
        kem.GenerateKeyPair(ek, dk);
        ek[0] = 0xFF;
        ek[1] = 0xFF;
        Assert.IsFalse(kem.TryEncapsulate(ek, new byte[ManagedMlKem768.CiphertextLength], new byte[32]));
        dk[(3 * 384) + 1184] ^= 0xFF;
        Assert.IsFalse(kem.TryDecapsulate(dk, new byte[ManagedMlKem768.CiphertextLength], new byte[32]));
    }

    private static int Pow(int value, int exponent)
    {
        int result = 1;
        while (exponent > 0)
        {
            if ((exponent & 1) == 1)
            {
                result = (int)((long)result * value % 3329);
            }

            value = (int)((long)value * value % 3329);
            exponent >>= 1;
        }

        return result;
    }

    private static int BitRev7(int value)
    {
        int reversed = 0;
        for (int bit = 0; bit < 7; bit++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }

        return reversed;
    }
}
