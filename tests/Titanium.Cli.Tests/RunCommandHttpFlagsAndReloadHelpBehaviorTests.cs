using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     ExecuteCore HTTP/2 and HTTP/3 flag edges plus process-level reload/restart help that still show thin on develop.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandHttpFlagsAndReloadHelpBehaviorTests
{
    // Bug class: a listener with enableHttp2:false (or server enableHttp3:false) is ignored by ExecuteCore, so the
    // process still negotiates those protocols against operators who opted out.
    [TestMethod]
    [Timeout(90_000)]
    public async Task ListenerHttp2OffAndServerHttp3Off_ThroughExecuteCore_BindsAndStops()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-h2h3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {port}
                decryptSsl: false
                enableHttp2: false
                enableHttp3: false
            server:
              enableHttp3: false
              drainTimeoutSeconds: 0
            """);

        var stdout = new StringWriter();
        var previousOut = Console.Out;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: true, serviceMode: false, cts.Token);
            await WaitForAsync(
                () => stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(35),
                () => stdout.ToString());

            Assert.IsTrue(await CanConnectAsync(port), "listener must bind with HTTP/2 and HTTP/3 disabled");

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(45)));
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            Console.SetOut(previousOut);
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    // Bug class: process-level reload/restart --help falls through into ExecuteCore and exits non-zero.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_ReloadAndRestartHelp_ExitZero()
    {
        var (reloadCode, reloadOut, reloadErr) = await RunCliAsync(["reload", "--help"], TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, reloadCode, reloadErr);
        StringAssert.Contains(reloadOut, "titanium reload");

        var (restartCode, restartOut, restartErr) = await RunCliAsync(["restart", "--help"], TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, restartCode, restartErr);
        StringAssert.Contains(restartOut, "titanium restart");
    }

    private static string FindTitaniumDll()
    {
        var testDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        foreach (var candidate in new[]
                 {
                     Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Release", "net10.0", "titanium.dll")),
                     Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Debug", "net10.0", "titanium.dll")),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail("titanium.dll not found; build Titanium.Cli first.");
        return "";
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCliAsync(string[] args, TimeSpan timeout)
    {
        var dll = FindTitaniumDll();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
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
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
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
            // Best-effort.
        }
    }
}
