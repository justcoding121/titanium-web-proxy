using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Titanium.E2E.Tests.Harness;

/// <summary>Spawns the titanium CLI from build output and tears it down.</summary>
public sealed class CliProcessHarness : IDisposable
{
    private Process? _process;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _gate = new();

    public string CliDirectory { get; }
    public string CliDllPath { get; }
    public string StdOut
    {
        get { lock (_gate) return _stdout.ToString(); }
    }

    public string StdErr
    {
        get { lock (_gate) return _stderr.ToString(); }
    }

    public int? ExitCode => _process is { HasExited: true } ? _process.ExitCode : null;

    public CliProcessHarness()
    {
        CliDirectory = LocateCliDirectory();
        CliDllPath = Path.Combine(CliDirectory, "titanium.dll");
        if (!File.Exists(CliDllPath))
        {
            throw new FileNotFoundException(
                "titanium.dll not found. Build Titanium.Cli (Release/Debug) before E2E tests.",
                CliDllPath);
        }
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void EnsurePlusDllBesideCli(bool copy)
    {
        var dest = Path.Combine(CliDirectory, "Titanium.Plus.dll");
        if (!copy)
        {
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }

            return;
        }

        var plus = LocatePlusDll();
        File.Copy(plus, dest, overwrite: true);
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunOnceAsync(
        string[] args,
        TimeSpan? timeout = null,
        IDictionary<string, string?>? env = null)
    {
        using var process = StartProcess(args, env);
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException($"CLI timed out. stdout={StdOut} stderr={StdErr}");
        }

        return (process.ExitCode, StdOut, StdErr);
    }

    public async Task StartRunAsync(
        string configPath,
        IDictionary<string, string?>? env = null,
        bool verbose = false)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        var args = new List<string> { "run", "-c", configPath };
        if (verbose)
        {
            args.Add("-v");
        }

        _process = StartProcess(args.ToArray(), env);
        await WaitForOutputAsync("running", TimeSpan.FromSeconds(45));
    }

    public async Task WaitForOutputAsync(string substring, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (StdOut.Contains(substring, StringComparison.OrdinalIgnoreCase) ||
                StdErr.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"CLI exited early ({_process.ExitCode}). stdout={StdOut} stderr={StdErr}");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Timed out waiting for '{substring}'. stdout={StdOut} stderr={StdErr}");
    }

    public void Dispose()
    {
        if (_process is null)
        {
            return;
        }

        TryKill(_process);
        _process.Dispose();
        _process = null;
    }

    private Process StartProcess(string[] args, IDictionary<string, string?>? env)
    {
        lock (_gate)
        {
            _stdout.Clear();
            _stderr.Clear();
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = CliDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(CliDllPath);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                if (v is null)
                {
                    psi.Environment.Remove(k);
                }
                else
                {
                    psi.Environment[k] = v;
                }
            }
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start CLI.");
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_gate) _stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_gate) _stderr.AppendLine(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string LocateCliDirectory()
    {
        var configs = new[] { "Release", "Debug" };
        var tfm = "net10.0";
        var repo = FindRepoRoot();
        foreach (var cfg in configs)
        {
            var dir = Path.Combine(repo, "src", "Titanium.Cli", "bin", cfg, tfm);
            if (File.Exists(Path.Combine(dir, "titanium.dll")))
            {
                return dir;
            }
        }

        // Fallback: adjacent to test assembly (project reference copies deps, not the exe layout)
        var testDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Release", tfm));
        if (File.Exists(Path.Combine(candidate, "titanium.dll")))
        {
            return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate Titanium.Cli output directory.");
    }

    private static string LocatePlusDll()
    {
        var repo = FindRepoRoot();
        foreach (var cfg in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(repo, "src", "Titanium.Plus", "bin", cfg, "net10.0", "Titanium.Plus.dll");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("Titanium.Plus.dll not found. Build Titanium.Plus first.");
    }

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Titanium.Web.Proxy.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Repo root not found from " + AppContext.BaseDirectory);
    }
}
