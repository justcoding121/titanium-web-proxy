using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
///     Pure proxy-wasm header map and limit parsing — no native runtime required.
///     Bug classes: Diff leaks pseudo-headers, Deserialize accepts truncated maps, limits accept out-of-range values.
/// </summary>
[TestClass]
public class WasmHeaderMapAndLimitsBehaviorTests
{
    [TestMethod]
    public void HeaderMap_Diff_MapsAuthorityToHost_AndJoinsRepeats()
    {
        var map = new WasmHeaderMap(
        [
            new(":method", "GET"),
            new(":authority", "old.example"),
            new("x-a", "1"),
            new("x-a", "2"),
        ]);

        map.Replace(":authority", "new.example");
        map.Remove("x-a");
        map.Add("x-b", "3");

        var (set, remove) = map.Diff();
        Assert.IsNotNull(set);
        Assert.AreEqual("new.example", set!["host"]);
        Assert.AreEqual("3", set["x-b"]);
        Assert.IsFalse(set.ContainsKey(":method"));
        Assert.IsNotNull(remove);
        CollectionAssert.Contains(remove!, "x-a");
    }

    [TestMethod]
    public void HeaderMap_SerializeDeserialize_RoundTrips_AndRejectsTruncation()
    {
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("host", "a"),
            new("x-trace", "1"),
        };
        var wire = WasmHeaderMap.Serialize(pairs);
        var back = WasmHeaderMap.Deserialize(wire);
        Assert.IsNotNull(back);
        Assert.AreEqual(2, back!.Count);
        Assert.AreEqual("host", back[0].Key);
        Assert.AreEqual("a", back[0].Value);

        Assert.AreEqual(0, WasmHeaderMap.Deserialize(ReadOnlySpan<byte>.Empty)!.Count);
        Assert.IsNull(WasmHeaderMap.Deserialize(new byte[] { 1, 0, 0 }));

        var truncated = wire.AsSpan(0, wire.Length - 2).ToArray();
        Assert.IsNull(WasmHeaderMap.Deserialize(truncated));

        var overCount = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(overCount, 99);
        Assert.IsNull(WasmHeaderMap.Deserialize(overCount));
    }

    [TestMethod]
    public void HeaderMap_Replace_KeepsOriginalSlotWhenPossible()
    {
        var map = new WasmHeaderMap([new("a", "1"), new("b", "2"), new("c", "3")]);
        map.Replace("b", "B");
        Assert.AreEqual("B", map.Get("b"));
        Assert.AreEqual("a", map.Pairs[0].Key);
        Assert.AreEqual("b", map.Pairs[1].Key);
        Assert.AreEqual("c", map.Pairs[2].Key);
    }

    [TestMethod]
    public void WasmLimits_TryParse_AppliesDefaultsAndRejectsOutOfRange()
    {
        Assert.IsTrue(WasmLimits.TryParse(new PluginConfig(), out var defaults, out var okError));
        Assert.IsNull(okError);
        Assert.AreEqual(WasmLimits.DefaultMemoryMb * 1024L * 1024, defaults.MemoryBytes);
        Assert.AreEqual(WasmLimits.DefaultCalloutConcurrency, defaults.CalloutConcurrency);

        Assert.IsFalse(WasmLimits.TryParse(new PluginConfig
        {
            Limits = new PluginLimitsConfig { MemoryMb = 0 },
        }, out _, out var memError));
        StringAssert.Contains(memError!, "memoryMb");

        Assert.IsFalse(WasmLimits.TryParse(new PluginConfig
        {
            Limits = new PluginLimitsConfig { Fuel = 0 },
        }, out _, out var fuelError));
        StringAssert.Contains(fuelError!, "fuel");

        Assert.IsFalse(WasmLimits.TryParse(new PluginConfig
        {
            Limits = new PluginLimitsConfig { PoolSize = 0 },
        }, out _, out var poolError));
        StringAssert.Contains(poolError!, "poolSize");

        Assert.IsFalse(WasmLimits.TryParse(new PluginConfig
        {
            Limits = new PluginLimitsConfig { EpochDeadlineMs = 0 },
        }, out _, out var epochError));
        StringAssert.Contains(epochError!, "epochDeadlineMs");

        Assert.IsFalse(WasmLimits.TryParse(new PluginConfig
        {
            Limits = new PluginLimitsConfig { CalloutConcurrency = -1 },
        }, out _, out var calloutError));
        StringAssert.Contains(calloutError!, "calloutConcurrency");

        Assert.IsTrue(WasmLimits.TryParse(new PluginConfig
        {
            TimeoutMs = 2500,
            Limits = new PluginLimitsConfig
            {
                MemoryMb = 32,
                Fuel = 1000,
                PoolSize = 2,
                CalloutConcurrency = 0,
                BufferedBodyBytes = 10,
                SharedDataBytes = 20,
                QueueBytes = 30,
                EpochDeadlineMs = 1500,
            },
        }, out var custom, out var customError));
        Assert.IsNull(customError);
        Assert.AreEqual(32 * 1024L * 1024, custom.MemoryBytes);
        Assert.AreEqual(1000, custom.Fuel);
        Assert.AreEqual(2, custom.PoolSize);
        Assert.AreEqual(0, custom.CalloutConcurrency);
        Assert.AreEqual(10, custom.BufferedBodyBytes);
        Assert.AreEqual(20, custom.SharedDataBytes);
        Assert.AreEqual(30, custom.QueueBytes);
        Assert.AreEqual(1500, custom.EpochDeadlineMs);
    }
}
