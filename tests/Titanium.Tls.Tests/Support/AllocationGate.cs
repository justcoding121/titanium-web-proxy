using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests.Support;

internal static class AllocationGate
{
    internal static void AssertNone(Action action, int warmup = 8, int iterations = 64)
    {
        for (int i = 0; i < warmup; i++)
        {
            action();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            action();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0, allocated);
    }
}
