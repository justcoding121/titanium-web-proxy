using System;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class FinishedTimingTests
{
    [TestMethod]
    [TestCategory("TimingSideChannel")]
    public void EqualAndUnequalFinishedPrefixesTakeTheSameShape()
    {
        byte[] left = new byte[32];
        byte[] right = new byte[32];
        left.AsSpan().Fill(0xA5);
        left.AsSpan().CopyTo(right);
        byte[] early = (byte[])right.Clone();
        early[0] ^= 0xFF;
        const int iterations = 4000;
        long equal = Time(left, right, iterations);
        long unequal = Time(left, early, iterations);
        long slower = Math.Max(equal, unequal);
        long faster = Math.Max(1, Math.Min(equal, unequal));
        Assert.IsTrue(slower < faster * 5, equal + " vs " + unequal);
    }

    private static long Time(byte[] left, byte[] right, int iterations)
    {
        bool match = CryptographicOperations.FixedTimeEquals(left, right);
        var watch = Stopwatch.StartNew();
        int ones = 0;
        for (int i = 0; i < iterations; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(left, right) == match)
            {
                ones++;
            }
        }

        watch.Stop();
        Assert.AreEqual(iterations, ones);
        return watch.ElapsedTicks;
    }
}
