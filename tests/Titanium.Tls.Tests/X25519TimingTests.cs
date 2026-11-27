using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
[DoNotParallelize]
public sealed class X25519TimingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("TimingSideChannel")]
    [Timeout(900000)]
    public void ScalarMultTimingDoesNotSeparateFixedFromRandom()
    {
        const int samples = 100_000;
        var fixedTicks = new long[samples];
        var randomTicks = new long[samples];
        byte[] fixedScalar = new byte[32];
        byte[] randomScalar = new byte[32];
        byte[] point = new byte[32];
        fixedScalar[0] = 9;
        point[0] = 9;
        var random = new Random(8446);
        Span<byte> result = stackalloc byte[32];
        X25519.ScalarMult(fixedScalar, point, result);
        for (int i = 0; i < samples; i++)
        {
            random.NextBytes(randomScalar);
            if (random.Next(2) == 0)
            {
                fixedTicks[i] = Time(fixedScalar, point, result);
                randomTicks[i] = Time(randomScalar, point, result);
            }
            else
            {
                randomTicks[i] = Time(randomScalar, point, result);
                fixedTicks[i] = Time(fixedScalar, point, result);
            }
        }

        long[] fixedCropped = CropTopFivePercent(fixedTicks);
        long[] randomCropped = CropTopFivePercent(randomTicks);
        double statistic = Welch(fixedCropped, randomCropped);
        double fixedUs = Mean(fixedCropped) * 1_000_000d / Stopwatch.Frequency;
        double randomUs = Mean(randomCropped) * 1_000_000d / Stopwatch.Frequency;
        TestContext.WriteLine("welch-t=" + statistic.ToString("F2") + " fixed-us=" + fixedUs.ToString("F1") + " random-us=" + randomUs.ToString("F1"));
        Assert.IsTrue(Math.Abs(statistic) <= 10, "Welch t is " + statistic.ToString("F2") + " (fixed " + fixedUs.ToString("F1") + " us, random " + randomUs.ToString("F1") + " us).");
    }

    private static long Time(byte[] scalar, byte[] point, Span<byte> result)
    {
        long start = Stopwatch.GetTimestamp();
        X25519.ScalarMult(scalar, point, result);
        return Stopwatch.GetTimestamp() - start;
    }

    private static long[] CropTopFivePercent(long[] samples)
    {
        Array.Sort(samples);
        int keep = samples.Length - (samples.Length / 20);
        var cropped = new long[keep];
        Array.Copy(samples, cropped, keep);
        return cropped;
    }

    private static double Welch(long[] left, long[] right)
    {
        double leftMean = Mean(left);
        double rightMean = Mean(right);
        double denominator = Math.Sqrt((Variance(left, leftMean) / left.Length) + (Variance(right, rightMean) / right.Length));
        return denominator == 0 ? 0 : (leftMean - rightMean) / denominator;
    }

    private static double Mean(long[] values)
    {
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum / values.Length;
    }

    private static double Variance(long[] values, double mean)
    {
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double delta = values[i] - mean;
            sum += delta * delta;
        }

        return sum / (values.Length - 1);
    }
}
