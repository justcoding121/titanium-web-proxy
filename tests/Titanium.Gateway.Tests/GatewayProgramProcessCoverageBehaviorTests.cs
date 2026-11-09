using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Process-level coverage for titanium-gateway Program top-level statements: bad args exit 2,
///     and missing kubeconfig exits 1 before any health wildcard bind (session may be firewall-locked).
/// </summary>
[TestClass]
[DoNotParallelize]
public class GatewayProgramProcessCoverageBehaviorTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_UnknownFlag_PrintsErrorAndExitsTwo()
    {
        var (code, _, stderr) = await RunGatewayAsync(["--bogus", "x"], TimeSpan.FromSeconds(20));
        Assert.AreEqual(2, code, stderr);
        StringAssert.Contains(stderr, "titanium-gateway:");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_MissingKubeConfig_ExitsOneWithoutBindingHealth()
    {
        // Bug class: a missing kubeconfig must fail before HealthServer binds a wildcard health prefix (firewall).
        var env = new Dictionary<string, string?>
        {
            ["KUBECONFIG"] = Path.Combine(Path.GetTempPath(), "twp-gw-missing-" + Guid.NewGuid().ToString("N")),
            ["TITANIUM_GATEWAY_DATA_PLANE_NAMESPACE"] = "default",
            ["TITANIUM_GATEWAY_DATA_PLANE_SERVICE"] = "titanium",
            ["TITANIUM_GATEWAY_NO_LEADER_ELECTION"] = "true",
            ["TITANIUM_GATEWAY_HEALTH_PORT"] = "18081",
            ["TITANIUM_GATEWAY_VERBOSE"] = "true",
            // Ignored service-link style variable exercises the warning branch before Create().
            ["TITANIUM_GATEWAY_DATA_PORT"] = "8080",
        };
        var (code, stdout, stderr) = await RunGatewayAsync([], TimeSpan.FromSeconds(30), env);
        Assert.AreEqual(1, code, stdout + Environment.NewLine + stderr);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunGatewayAsync(
        string[] args,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? extraEnv = null)
    {
        var dll = LocateGatewayDll();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { dll },
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (extraEnv is not null)
        {
            foreach (var pair in extraEnv)
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start titanium-gateway");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort
            }

            Assert.Fail("titanium-gateway did not exit");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static string LocateGatewayDll()
    {
        var probe = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                    ?? throw new InvalidOperationException("test assembly location missing");
        // Prefer the Gateway project output (full deps) over the thin copy next to the test DLL.
        for (var depth = 0; depth < 10; depth++)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(
                    probe,
                    "src",
                    "Titanium.Gateway",
                    "bin",
                    config,
                    "net10.0",
                    "titanium-gateway.dll");
                if (File.Exists(candidate) && File.Exists(Path.ChangeExtension(candidate, ".deps.json")))
                {
                    return candidate;
                }
            }

            var parent = Directory.GetParent(probe);
            if (parent is null)
            {
                break;
            }

            probe = parent.FullName;
        }

        throw new FileNotFoundException("titanium-gateway.dll (with deps.json) not found under src/Titanium.Gateway/bin");
    }
}
