using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliScriptingE2ETests
{
    private const string LuaHeaderSource =
        "function on_request_headers(ctx)\n  ctx.set_header(\"x-twp-e2e-script\", \"1\")\nend\n";

    private const string WasmNoopSource = """
        (module
          (memory (export "memory") 2)
          (global $heap (mut i32) (i32.const 8192))
          (func (export "proxy_abi_version_0_2_1"))
          (func (export "proxy_on_memory_allocate") (param $size i32) (result i32)
            (local $p i32)
            (local.set $p (global.get $heap))
            (global.set $heap (i32.and (i32.add (i32.add (global.get $heap) (local.get $size)) (i32.const 7)) (i32.const -8)))
            (local.get $p))
          (func (export "proxy_on_context_create") (param i32 i32))
          (func (export "proxy_on_vm_start") (param i32 i32) (result i32) (i32.const 1))
          (func (export "proxy_on_configure") (param i32 i32) (result i32) (i32.const 1))
          (func (export "proxy_on_request_headers") (param i32 i32 i32) (result i32) (i32.const 0))
          (func (export "proxy_on_done") (param i32) (result i32) (i32.const 1))
          (func (export "proxy_on_delete") (param i32))
        )
        """;

    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            /* ignore */
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_LuaHeaderPlugin_StampsRequestHeader()
    {
        if (!ScriptingNativesPresent(lua: true))
            Assert.Inconclusive("Lua scripting native not beside the CLI (bundle-scripting-native.ps1).");

        using var origin = new HeaderEchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteScriptPlugin(
            _tempDir, listen, origin.Port, "lua", "script.lua", LuaHeaderSource);
        using var harness = new CliProcessHarness();
        await harness.StartRunAsync(cfg);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{listen}/lua");
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "x-twp-e2e-script:1");
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_WasmNoopPlugin_ProxiesOk()
    {
        if (!ScriptingNativesPresent(wasm: true))
            Assert.Inconclusive("Wasmtime native not beside the CLI (bundle-scripting-native.ps1).");

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteScriptPlugin(
            _tempDir, listen, origin.Port, "wasm", "script.wat", WasmNoopSource);
        using var harness = new CliProcessHarness();
        await harness.StartRunAsync(cfg);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{listen}/wasm");
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_LuaPlugin_WithoutNative_FailsClosed()
    {
        using var isolated = CliProcessHarness.CreateIsolatedCopy(copyPlus: false);
        foreach (var name in new[] { "lua54.dll", "liblua54.so", "liblua54.dylib", "lua54.dll" })
        {
            var p = Path.Combine(isolated.CliDirectory, name);
            if (File.Exists(p))
                File.Delete(p);
        }

        // Also remove common shim names if present.
        foreach (var file in Directory.EnumerateFiles(isolated.CliDirectory, "*lua*"))
        {
            try { File.Delete(file); } catch { /* ignore */ }
        }

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteScriptPlugin(
            _tempDir, listen, origin.Port, "lua", "script.lua", LuaHeaderSource);

        try
        {
            await isolated.StartRunAsync(cfg);
        }
        catch (InvalidOperationException)
        {
            // Start refused when the plugin cannot load — fail-closed at boot.
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{listen}/x");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }
        finally
        {
            isolated.Dispose();
        }
    }

    private static bool ScriptingNativesPresent(bool lua = false, bool wasm = false)
    {
        using var h = new CliProcessHarness();
        var dir = h.CliDirectory;
        if (lua)
        {
            return File.Exists(Path.Combine(dir, "lua54.dll"))
                   || File.Exists(Path.Combine(dir, "liblua54.so"))
                   || File.Exists(Path.Combine(dir, "liblua54.dylib"));
        }

        if (wasm)
        {
            return File.Exists(Path.Combine(dir, "wasmtime.dll"))
                   || File.Exists(Path.Combine(dir, "libwasmtime.so"))
                   || File.Exists(Path.Combine(dir, "libwasmtime.dylib"));
        }

        return false;
    }
}

/// <summary>Echoes request headers named x-twp-* into the response body.</summary>
internal sealed class HeaderEchoOrigin : IDisposable
{
    private readonly HttpListener _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; }

    public HeaderEchoOrigin()
    {
        (_listener, Port) = CliProcessHarness.BindHttpListenerOrRetry(p => $"http://127.0.0.1:{p}/");
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            /* ignore */
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var parts = new List<string>();
                    foreach (string? key in ctx.Request.Headers.AllKeys ?? [])
                    {
                        if (key is null) continue;
                        if (!key.StartsWith("x-twp-", StringComparison.OrdinalIgnoreCase)) continue;
                        parts.Add($"{key.ToLowerInvariant()}:{ctx.Request.Headers[key]}");
                    }

                    var body = System.Text.Encoding.UTF8.GetBytes(string.Join(',', parts));
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.ContentLength64 = body.Length;
                    ctx.Response.OutputStream.Write(body);
                    ctx.Response.Close();
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { /* ignore */ }
                }
            }, ct);
        }
    }
}
