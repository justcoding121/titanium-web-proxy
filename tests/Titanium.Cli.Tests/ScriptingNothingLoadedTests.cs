using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Scripting;

namespace Titanium.Cli.Tests;

/// <summary>
/// N7: a configuration with no <c>kind: lua</c> or <c>kind: wasm</c> plugin pays nothing for scripting. The engines are
/// registered (the CLI does this at start) but lazy, so neither <c>lua54</c> nor <c>wasmtime</c> is ever loaded into the
/// process, and nothing even asks to. This runs the real CLI lifecycle (<c>ExecuteCoreAsync</c>) on a default reverse
/// configuration and serves a request through it before checking.
/// <para>
///     No test in this assembly names a script kind with a real engine, so nothing else loads the natives before this
///     one runs. Keep it that way: a test that does belongs in <c>Titanium.Web.Proxy.Scripting.Tests</c>.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class ScriptingNothingLoadedTests
{
    private static readonly string[] NativeStems = ["lua54", "wasmtime"];

    [TestInitialize]
    public void Init() => ScriptingBootstrap.Register();

    [TestCleanup]
    public void Cleanup()
    {
        PluginEngines.Unregister(PluginKinds.Lua);
        PluginEngines.Unregister(PluginKinds.Wasm);
    }

    [TestMethod]
    public void Registration_IsLazy_AndLoadsNothing()
    {
        Assert.IsTrue(PluginEngines.IsRegistered(PluginKinds.Lua));
        Assert.IsTrue(PluginEngines.IsRegistered(PluginKinds.Wasm));

        AssertNothingLoaded("after ScriptingBootstrap.Register()");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task DefaultReverseConfig_ServesTraffic_WithoutLoadingLuaOrWasmtime()
    {
        using var origin = new TinyOrigin();
        var port = FreePort();
        var dir = Path.Combine(Path.GetTempPath(), "twp-n7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $$"""
            schemaVersion: "7.1"
            logging:
              enabled: false
              enableConsole: false
            listeners:
              - host: "127.0.0.1"
                port: {{port}}
                decryptSsl: false
                enableHttp2: false
            routes:
              - id: r1
                clusterId: c1
                match: { path: /, pathKind: Prefix }
            clusters:
              - id: c1
                destinations:
                  - id: d1
                    address: "127.0.0.1"
                    port: {{origin.Port}}
            """);

        var cwd = Directory.GetCurrentDirectory();
        using var stop = new CancellationTokenSource();
        try
        {
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: true, stop.Token);

            var served = false;
            for (var attempt = 0; attempt < 40 && !served; attempt++)
            {
                await Task.Delay(250);
                served = await TryGetAsync(port);
            }

            Assert.IsTrue(served, "the default reverse configuration never answered a request");
            AssertNothingLoaded("after serving a request on a default reverse configuration");

            await stop.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
            AssertNothingLoaded("after the proxy stopped");
        }
        finally
        {
            await stop.CancelAsync();
            Directory.SetCurrentDirectory(cwd);
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static void AssertNothingLoaded(string when)
    {
        var modules = LoadedModuleNames();
        if (modules is not null)
        {
            // Proves the enumeration works, so "nothing found" is not just an empty list.
            Assert.IsTrue(modules.Any(m => m.Contains("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)),
                "the module list did not contain the runtime itself; the check below would be vacuous");

            foreach (var stem in NativeStems)
            {
                var hit = modules.FirstOrDefault(m => m.Contains(stem, StringComparison.OrdinalIgnoreCase));
                Assert.IsNull(hit, $"{when}: the native library '{hit}' is loaded in the process");
            }
        }

        // The engines record that anything asked for their native, so this also holds where the module list is not available.
        Assert.IsFalse(AttemptedFlag("Titanium.Web.Proxy.Scripting.Lua.Native.LuaNative"), $"{when}: the Lua native was requested");
        Assert.IsFalse(AttemptedFlag("Titanium.Web.Proxy.Scripting.Wasm.WasmRuntime"), $"{when}: Wasmtime was requested");
    }

    /// <summary>Module names of this process, or null where the OS offers no list (macOS).</summary>
    private static List<string>? LoadedModuleNames()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.Modules.Cast<ProcessModule>().Select(m => m.FileName).ToList();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool AttemptedFlag(string typeName)
    {
        var type = typeof(ScriptingBootstrap).Assembly.GetType(typeName, throwOnError: true)!;
        var property = type.GetProperty("LoadAttempted", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                       ?? throw new MissingMemberException(typeName, "LoadAttempted");
        return (bool)property.GetValue(null)!;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> TryGetAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/");
            return response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == "ok";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>A loopback HTTP/1.1 origin that answers every request with 200 and the body "ok".</summary>
    private sealed class TinyOrigin : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public TinyOrigin()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var seen = new StringBuilder();
                    while (!_stop.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                        {
                            return;
                        }

                        seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        while (seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            var all = seen.ToString();
                            seen.Clear().Append(all[(all.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
                            await stream.WriteAsync(
                                Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Type: text/plain\r\n\r\nok"),
                                _stop.Token);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    // the client went away
                }
            }
        }
    }
}
