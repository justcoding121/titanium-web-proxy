using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     Process-level E2E for <c>titanium run</c> / <c>titanium reload</c> through the published
///     <c>titanium.dll</c> entrypoint. Hits <see cref="RunCommand.ExecuteAsync"/> argument parsing and the
///     Windows reload named-event path the in-process tests cannot exercise alone.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandProcessE2EBehaviorTests
{
    // Bug class: `titanium run --help` falls through into ExecuteCore (needs a config) or exits non-zero.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_RunHelp_PrintsUsageAndExitsZero()
    {
        var (code, stdout, stderr) = await RunCliAsync(["run", "--help"], TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, code, stderr);
        StringAssert.Contains(stdout, "titanium run");
        StringAssert.Contains(stdout, "-c");
    }

    // Bug class: a real process never registers the Windows reload event (or reload cannot find it), so
    // operators get "No running titanium process" even while `run` is serving.
    [TestMethod]
    [Timeout(120_000)]
    public async Task Process_RunThenReload_SignalsAndKeepsServing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named EventWaitHandle reload is Windows-only.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "twp-run-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, ListenerYaml(port, localZone: "before"));

        Process? proc = null;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            proc = StartCli(["run", "-c", path, "-v"], stdout, stderr);

            await WaitForAsync(
                () => stdout.ToString().Contains("windows-reload-handler-registered", StringComparison.Ordinal)
                      || stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(45),
                () => stdout + Environment.NewLine + stderr);
            Assert.IsTrue(await CanConnectAsync(port), "process listener must be up before reload");

            await File.WriteAllTextAsync(path, ListenerYaml(port, localZone: "after-e2e-reload"));
            var (reloadCode, reloadOut, reloadErr) = await RunCliAsync(
                ["reload", "-c", path], TimeSpan.FromSeconds(30));
            Assert.AreEqual(0, reloadCode, reloadErr);
            StringAssert.Contains(reloadOut + reloadErr, "Reload signaled");

            await WaitForAsync(
                () => stdout.ToString().Contains("Config reloaded.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(40),
                () => stdout + Environment.NewLine + stderr);
            Assert.IsTrue(await CanConnectAsync(port), "listener must keep serving across process reload");
        }
        finally
        {
            await StopProcessAsync(proc);
            ConfigReloadGate.TryDeletePidFile(path);
            TryDelete(dir);
        }
    }

    // Bug class: reload without a waiter returns 0 / hangs instead of telling the operator to start run first.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_ReloadWithoutWaiter_ExitsOneWithActionableMessage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-reload-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "missing-waiter.yaml");
        await File.WriteAllTextAsync(path, ListenerYaml(FreePort(), localZone: "none"));
        try
        {
            ConfigReloadGate.TryDeletePidFile(path);
            var (code, _, stderr) = await RunCliAsync(["reload", "-c", path], TimeSpan.FromSeconds(30));
            Assert.AreEqual(1, code, stderr);
            StringAssert.Contains(stderr, "No running titanium process");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // Bug class: invalid YAML exits 0 or prints nothing actionable when launched as a real process.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_RunInvalidConfig_ExitsOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-bad-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bad.yaml");
        await File.WriteAllTextAsync(path, "listeners: [this is not valid");
        try
        {
            var (code, _, stderr) = await RunCliAsync(["run", "-c", path], TimeSpan.FromSeconds(30));
            Assert.AreEqual(1, code, stderr);
            Assert.IsFalse(string.IsNullOrWhiteSpace(stderr), "operator needs an error on stderr");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static string ListenerYaml(int port, string localZone) => $"""
        schemaVersion: "7.1"
        listeners:
          - host: "127.0.0.1"
            port: {port}
            decryptSsl: false
        server:
          drainTimeoutSeconds: 0
          localZone: "{localZone}"
        """;

    private static string FindTitaniumDll()
    {
        // Prefer the Cli project output (full deps). The test-host copy of titanium.dll often lacks
        // satellite assemblies and fails with FileNotFoundException on Microsoft.Extensions.*.
        var testDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Release", "net10.0", "titanium.dll")),
            Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Debug", "net10.0", "titanium.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "titanium.dll")),
            typeof(RunCommand).Assembly.Location,
        };
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate) ||
                !Path.GetFileName(candidate).Equals("titanium.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var deps = Path.Combine(Path.GetDirectoryName(candidate)!, "titanium.deps.json");
            if (File.Exists(deps) || candidate.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}Titanium.Cli{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        Assert.Fail("titanium.dll with runtime deps not found; build Titanium.Cli first.");
        return "";
    }

    private static ProcessStartInfo CreatePsi(string[] args)
    {
        var dll = FindTitaniumDll();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static Process StartCli(string[] args, StringBuilder stdout, StringBuilder stderr)
    {
        var proc = Process.Start(CreatePsi(args))
                   ?? throw new InvalidOperationException("Failed to start titanium.dll");
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCliAsync(
        string[] args,
        TimeSpan timeout)
    {
        using var proc = Process.Start(CreatePsi(args))
                         ?? throw new InvalidOperationException("Failed to start titanium.dll");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort.
            }

            Assert.Fail($"CLI timed out: dotnet titanium.dll {string.Join(' ', args)}");
        }

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task StopProcessAsync(Process? proc)
    {
        if (proc is null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        catch
        {
            // Process may already be gone.
        }
        finally
        {
            proc.Dispose();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForAsync(Func<bool> ready, TimeSpan timeout, Func<string> detail)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ready())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(detail());
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // The process may still hold a handle briefly.
        }
    }
}
