using System;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class X25519DifferentialTests
{
    [TestMethod]
    public void ScalarMultMatchesBouncyCastleOnThirtyTwoPairs()
    {
        AssertMatches(32);
    }

    [TestMethod]
    [TestCategory("Slow")]
    public void ScalarMultMatchesBouncyCastleOnTenThousandPairs()
    {
        AssertMatches(10_000);
    }

    private static void AssertMatches(int pairs)
    {
        byte[] scalar = new byte[32];
        byte[] point = new byte[32];
        byte[] ours = new byte[32];
        byte[] theirs = new byte[32];
        for (int i = 0; i < pairs; i++)
        {
            RandomNumberGenerator.Fill(scalar);
            RandomNumberGenerator.Fill(point);
            point[31] &= 127;
            X25519.ScalarMult(scalar, point, ours);
            Org.BouncyCastle.Math.EC.Rfc7748.X25519.ScalarMult(scalar, 0, point, 0, theirs, 0);
            Assert.IsTrue(ours.AsSpan().SequenceEqual(theirs), $"pair {i}");
        }
    }
}
