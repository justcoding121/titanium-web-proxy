using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Wasm;
using static Titanium.Web.Proxy.Scripting.Tests.Wasm.WasmTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
/// The conformance harness: every sample filter is listed here with what the host must do with it, and each one that
/// loads is driven through a full HTTP request and a full stream connection with neutral input. Whatever a filter does
/// with that input, the host must not fault, must not leave the request paused, and must end every context it started.
/// A new sample must be added to <see cref="Expectations"/>, so no filter ships untested.
/// </summary>
[TestClass]
[DoNotParallelize]
public class WasmConformanceTests
{
    /// <summary>Sample file, then the ABI it must load as, or null with the error it must fail with.</summary>
    private static readonly (string File, string? Abi, string? Error)[] Expectations =
    [
        ("headers_filter.wat", ProxyWasmAbi.V021, null),
        ("faults.wat", ProxyWasmAbi.V021, null),
        ("abi_0_1_0.wat", ProxyWasmAbi.V010, null),
        ("callouts.wat", ProxyWasmAbi.V021, null),
        ("state.wat", ProxyWasmAbi.V021, null),
        ("tcp_filter.wat", ProxyWasmAbi.V021, null),
        ("wasi_exit0.wat", ProxyWasmAbi.V020, null),
        ("missing_import.wat", null, "missing import env.proxy_teleport"),
        ("unknown_abi.wat", null, "proxy_abi_version_9_9_9"),
        ("wasi_exit1.wat", null, "proc_exit(3)"),
    ];

    [TestMethod]
    public void EverySampleHasAnExpectation()
    {
        var files = Directory.GetFiles(Path.GetDirectoryName(Sample("x"))!, "*.wat").Select(Path.GetFileName).Order().ToArray();

        CollectionAssert.AreEquivalent(files, Expectations.Select(e => e.File).ToArray());
    }

    [TestMethod]
    public void SamplesThatMustNotLoad_FailWithAClearError()
    {
        foreach (var (file, _, error) in Expectations.Where(e => e.Error is not null))
        {
            StringAssert.Contains(CreateError(Config(file)), error, file);
        }
    }

    [TestMethod]
    public async Task SamplesThatLoad_SurviveAFullRequestAndConnection_WithoutFaultsOrLeaks()
    {
        foreach (var (file, abi, _) in Expectations.Where(e => e.Abi is not null))
        {
            using var plugin = Create(Config(file, poolSize: 2));
            Assert.AreEqual(abi, plugin.Pool.Module.AbiVersion, file);

            await DriveRequestAsync(plugin, file);
            await DriveConnectionAsync(plugin, file);

            Assert.AreEqual(2, plugin.Pool.HealthyCount, $"{file}: a neutral request must not fault a VM");
            foreach (var vm in plugin.Pool.Vms)
            {
                Assert.AreEqual(1, vm!.Contexts.Count(), $"{file}: only the root context is left in each VM");
            }
        }
    }

    private static async Task DriveRequestAsync(WasmPlugin plugin, string file)
    {
        IScriptPhaseHost host = plugin;
        var items = new Dictionary<string, object?>();
        var body = Encoding.ASCII.GetBytes("body");
        var results = new List<ScriptPhaseResult>
        {
            await host.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, items, [H("content-length", "4")], method: "POST"), default),
            await host.OnRequestBodyAsync(Phase(ScriptPhases.RequestBody, items, chunk: body, last: true), default),
            await host.OnRequestTrailersAsync(Phase(ScriptPhases.RequestTrailers, items), default),
            await host.OnResponseHeadersAsync(Phase(ScriptPhases.ResponseHeaders, items, [H("content-length", "4")], status: 200), default),
            await host.OnResponseBodyAsync(Phase(ScriptPhases.ResponseBody, items, chunk: body, last: true), default),
            await host.OnResponseTrailersAsync(Phase(ScriptPhases.ResponseTrailers, items), default),
        };
        await host.OnLogAsync(Phase(ScriptPhases.Log, items, status: 200), default);

        foreach (var result in results)
        {
            Assert.AreNotEqual(ScriptAction.Pause, result.Action, $"{file}: neutral input must not leave the request paused");
            Assert.AreNotEqual(503, result.LocalResponse?.Status, $"{file}: the filter faulted");
        }

        Assert.AreEqual(0, items.Count, $"{file}: the log phase ends the HTTP context");
    }

    private static async Task DriveConnectionAsync(WasmPlugin plugin, string file)
    {
        IStreamScriptFilter filter = plugin;
        var connection = new StreamConnectionContext
        {
            ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 40000),
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 443),
            Items = new Dictionary<string, object?>(),
        };

        var results = new[]
        {
            await filter.OnNewConnectionAsync(connection, default),
            await filter.OnDownstreamDataAsync(connection, Encoding.ASCII.GetBytes("hello"), default),
            await filter.OnUpstreamDataAsync(connection, Encoding.ASCII.GetBytes("world"), default),
        };
        await filter.OnCloseAsync(connection);

        foreach (var result in results)
        {
            Assert.AreEqual(ScriptAction.Continue, result.Action, $"{file}: neutral data must pass");
        }

        Assert.AreEqual(0, connection.Items.Count, $"{file}: close ends the stream context");
    }
}
