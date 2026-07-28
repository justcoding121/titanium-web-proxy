using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3.Qpack;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for <see cref="QpackDynamicTable" />.
/// </summary>
[TestClass]
public class QpackDynamicTableTests
{
    [TestMethod]
    public void Insert_And_LookupByAbsoluteIndex_RoundTrip()
    {
        using var table = new QpackDynamicTable(4096);

        var idx = table.Insert("content-type", "application/json");

        Assert.AreEqual(0UL, idx, "First absolute index must be 0.");
        Assert.IsTrue(table.TryGetByAbsoluteIndex(0, out var name, out var value));
        Assert.AreEqual("content-type", name);
        Assert.AreEqual("application/json", value);
    }

    [TestMethod]
    public void Insert_MultipleEntries_AbsoluteIndicesAreMonotonic()
    {
        using var table = new QpackDynamicTable(4096);

        var idx0 = table.Insert("a", "1");
        var idx1 = table.Insert("b", "2");
        var idx2 = table.Insert("c", "3");

        Assert.AreEqual(0UL, idx0);
        Assert.AreEqual(1UL, idx1);
        Assert.AreEqual(2UL, idx2);
        Assert.AreEqual(3UL, table.InsertCount);
    }

    [TestMethod]
    public void LookupByAbsoluteIndex_EvictedEntry_ReturnsFalse()
    {
        // Each entry costs name(1) + value(1) + 32 = 34 bytes per RFC 9204 §3.2.1.
        // Capacity = 34: exactly one entry fits, so inserting a second evicts the first.
        using var table = new QpackDynamicTable(34);

        table.Insert("a", "1"); // absolute index 0 — will be evicted
        table.Insert("b", "2"); // absolute index 1 — evicts index 0

        Assert.IsFalse(table.TryGetByAbsoluteIndex(0, out _, out _),
            "Evicted entry should not be found.");
        Assert.IsTrue(table.TryGetByAbsoluteIndex(1, out var name, out _));
        Assert.AreEqual("b", name);
    }

    [TestMethod]
    public void LookupByAbsoluteIndex_OutOfRange_ReturnsFalse()
    {
        using var table = new QpackDynamicTable(4096);
        table.Insert("x", "y");

        Assert.IsFalse(table.TryGetByAbsoluteIndex(99, out _, out _));
    }

    [TestMethod]
    public void TryFind_ExactMatch_ReturnsCorrectAbsoluteIndex()
    {
        using var table = new QpackDynamicTable(4096);
        table.Insert("content-type", "text/plain");
        table.Insert("content-type", "application/json");

        var found = table.TryFind("content-type", "application/json", out var absIdx, out var exact);

        Assert.IsTrue(found);
        Assert.IsTrue(exact);
        Assert.AreEqual(1UL, absIdx);
    }

    [TestMethod]
    public void TryFind_NameOnlyMatch_ReturnsFirstOccurrence()
    {
        using var table = new QpackDynamicTable(4096);
        table.Insert("accept", "text/html");

        var found = table.TryFind("accept", "application/json", out var absIdx, out var exact);

        Assert.IsTrue(found);
        Assert.IsFalse(exact);
        Assert.AreEqual(0UL, absIdx);
    }

    [TestMethod]
    public void TryFind_NoMatch_ReturnsFalse()
    {
        using var table = new QpackDynamicTable(4096);
        table.Insert("content-type", "text/plain");

        var found = table.TryFind("accept", "application/json", out _, out _);

        Assert.IsFalse(found);
    }

    [TestMethod]
    public void SetCapacity_ReducedCapacity_EvictsOldest()
    {
        using var table = new QpackDynamicTable(4096);
        table.Insert("a", "1"); // abs index 0
        table.Insert("b", "2"); // abs index 1
        table.Insert("c", "3"); // abs index 2

        // Force capacity so small that only the newest entry fits.
        table.SetCapacity(34); // one entry (1+1+32=34)

        Assert.IsFalse(table.TryGetByAbsoluteIndex(0, out _, out _), "Entry 0 evicted.");
        Assert.IsFalse(table.TryGetByAbsoluteIndex(1, out _, out _), "Entry 1 evicted.");
        Assert.IsTrue(table.TryGetByAbsoluteIndex(2, out var name, out _));
        Assert.AreEqual("c", name, "Only newest entry survives.");
    }

    [TestMethod]
    public void Insert_WithInFlightProtection_DoesNotEvictPinnedEntry()
    {
        // Capacity: 68 bytes (exactly 2 entries of 34 bytes each).
        // After inserting 2, the 3rd insert normally evicts the oldest.
        // With the in-flight pin on abs 0, it cannot evict.
        using var table = new QpackDynamicTable(68);

        table.Insert("a", "1"); // abs 0 — will be pinned
        table.Insert("b", "2"); // abs 1

        var inFlight = new ConcurrentDictionary<long, ulong>();
        inFlight[42L] = 0UL; // stream 42 references abs index 0

        // Insert a 3rd entry: would evict abs 0, but it is pinned.
        table.Insert("c", "3", inFlight);

        // Abs index 0 must still be readable (eviction was skipped).
        Assert.IsTrue(table.TryGetByAbsoluteIndex(0, out var name, out _));
        Assert.AreEqual("a", name);
    }

    [TestMethod]
    public void Insert_WithoutInFlightProtection_EvictsOldestNormally()
    {
        // 68 bytes = exactly 2 entries; the 3rd evicts abs 0.
        using var table = new QpackDynamicTable(68);

        table.Insert("a", "1"); // abs 0
        table.Insert("b", "2"); // abs 1

        // No in-flight protection — insert should evict abs 0.
        table.Insert("c", "3");

        Assert.IsFalse(table.TryGetByAbsoluteIndex(0, out _, out _), "Abs 0 should be evicted.");
        Assert.IsTrue(table.TryGetByAbsoluteIndex(2, out var name, out _));
        Assert.AreEqual("c", name);
    }
}
