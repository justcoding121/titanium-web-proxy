using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
///     Remaining WasmHeaderMap leftovers toward 90%: Diff joins repeated values, SetAll
///     replaces the map, and Replace on a missing name appends.
/// </summary>
[TestClass]
public class WasmHeaderMapDiffJoinAndSetAllBehaviorTests
{
    [TestMethod]
    public void Diff_JoinsRepeatedValues_AndMapsAuthorityToHost()
    {
        var map = new WasmHeaderMap(
        [
            new(":authority", "old.example"),
            new(":method", "GET"),
            new("x-a", "1"),
        ]);
        map.Add("x-a", "2");
        map.Replace(":authority", "new.example");

        var (set, remove) = map.Diff();
        Assert.IsNotNull(set);
        Assert.AreEqual("new.example", set!["host"]);
        Assert.AreEqual("1, 2", set["x-a"]);
        Assert.IsFalse(set.ContainsKey(":method"));
        Assert.IsNull(remove);
    }

    [TestMethod]
    public void SetAll_AndReplaceMissing_Append()
    {
        var map = new WasmHeaderMap([new("a", "1")]);
        map.SetAll(
        [
            new("B", "2"),
            new("c", "3"),
        ]);
        Assert.AreEqual(2, map.Count);
        Assert.IsNull(map.Get("a"));
        Assert.AreEqual("2", map.Get("b"));

        map.Replace("missing", "x");
        Assert.AreEqual("x", map.Get("missing"));
        Assert.AreEqual(3, map.Count);

        var (set, remove) = map.Diff();
        Assert.IsNotNull(set);
        Assert.IsNotNull(remove);
        CollectionAssert.Contains(remove!, "a");
        Assert.AreEqual("2", set!["b"]);
        Assert.AreEqual("x", set["missing"]);
    }
}
