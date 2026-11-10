using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Lua;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Remaining LuaOptions shared-dict / buffered bodyMode success path and LuaCodec depth /
///     unknown-tag / AsInt byte-parse leftovers still thin on Sonar.
/// </summary>
[TestClass]
public class LuaOptionsSharedDictAndCodecDepthBehaviorTests
{
    [TestMethod]
    public void TryParse_BufferedBodyMode_AndValidSharedDicts()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-lua-dict-" + Guid.NewGuid().ToString("N") + ".lua");
        File.WriteAllText(path, "-- ok");
        try
        {
            Assert.IsTrue(LuaOptions.TryParse(
                new PluginConfig
                {
                    Name = "buf",
                    File = path,
                    BodyMode = "buffered",
                    FailMode = ProxyPluginContract.FailOpen,
                    Settings = new Dictionary<string, string>
                    {
                        [LuaOptions.SharedDictsSetting] = "cache:100,metrics",
                    },
                },
                out var options,
                out var error));
            Assert.IsNull(error);
            Assert.AreEqual(ScriptBodyMode.Buffered, options!.BodyMode);
            Assert.IsTrue(options.FailOpen);
            Assert.AreEqual(2, options.SharedDicts.Count);
            Assert.AreEqual(("cache", 100L), options.SharedDicts[0]);
            Assert.AreEqual(("metrics", 10_000L), options.SharedDicts[1]);

            Assert.IsFalse(LuaOptions.TryParse(
                new PluginConfig
                {
                    Name = "bad",
                    File = path,
                    Settings = new Dictionary<string, string>
                    {
                        [LuaOptions.SharedDictsSetting] = "ok:0",
                    },
                },
                out _,
                out var badCount));
            StringAssert.Contains(badCount!, "max item count");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    [TestMethod]
    public void LuaCodec_NestedDepth_UnknownTag_AndAsStringAsIntEdges()
    {
        Assert.AreEqual("true", LuaReader.AsString(true));
        Assert.AreEqual("false", LuaReader.AsString(false));
        Assert.AreEqual("-3", LuaReader.AsString(-3L));
        Assert.IsNull(LuaReader.AsString(new object()));
        Assert.AreEqual(11L, LuaReader.AsInt("11"u8.ToArray()));
        Assert.IsNull(LuaReader.AsInt("nope"u8.ToArray()));
        Assert.IsNull(LuaReader.AsInt(1.25));

        Assert.ThrowsExactly<FormatException>(() => _ = new LuaReader([(byte)'Q']).Read());
        Assert.ThrowsExactly<FormatException>(() => _ = new LuaReader([(byte)'i']).Read());

        // Depth over MaxDepth while reading a nested table.
        var writer = new LuaWriter();
        void Nest(int depth)
        {
            if (depth == 0)
            {
                writer.Nil();
                return;
            }

            var mark = writer.BeginTable();
            writer.Int(1);
            Nest(depth - 1);
            writer.EndTable(mark, 1);
        }

        Nest(40);
        Assert.ThrowsExactly<FormatException>(() => _ = new LuaReader(writer.ToArray()).Read());

        var skip = new LuaWriter();
        skip.String("x");
        var reader = new LuaReader(skip.ToArray());
        Assert.AreEqual(-1, reader.ReadTableHeader());
        CollectionAssert.AreEqual("x"u8.ToArray(), (byte[]?)new LuaReader(skip.ToArray()).Read());
    }
}
