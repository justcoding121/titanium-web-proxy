using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;
using static Titanium.Web.Proxy.Scripting.Tests.Wasm.WasmTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>Host imports a guest can call that the other samples skip: each must answer with the ABI's result code, never fault.</summary>
[TestClass]
[DoNotParallelize]
public class WasmHostCallsBehaviorTests
{
    // One character per call in host_calls.wat ('0' + result code): see the sample's header for the call behind each index.
    // 0 Ok, 1 NotFound, 2 BadArgument, 4 ParseFailure.
    private const string ExpectedResults = "000000111001100200004011110111114";

    [TestMethod]
    public async Task HostImports_AnswerWithAbiResultCodes_AndKeepTheVmHealthy()
    {
        using var plugin = Create(Config("host_calls.wat", poolSize: 1));

        var result = await plugin.OnRequestHeadersAsync(
            Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(), [H("x-a", "old"), H("x-keep", "k")]), default);

        Assert.AreEqual(ScriptAction.Continue, result.Action);
        Assert.AreEqual(ExpectedResults, result.SetHeaders!["x-results"]);

        // set_header_map_pairs replaced the whole map with one pair, so the original headers are gone.
        Assert.AreEqual("v", result.SetHeaders["x-set"]);
        CollectionAssert.IsSubsetOf(new[] { "x-a", "x-keep" }, result.RemoveHeaders!.ToArray());
    }
}
