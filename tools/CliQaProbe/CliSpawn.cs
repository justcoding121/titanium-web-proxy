using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Titanium.Cli.QaProbe;

/// <summary>
/// Spawns the built CLI apphost (<c>titanium.exe</c> / <c>titanium</c>) so
/// <c>service install</c> records a real SCM / systemd / launchd binPath.
/// </summary>
public sealed class CliSpawn : IDisposable
{
    private Process? _runProcess;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _gate = new();

    public string CliDirectory { get; }
    public string CliDllPath { get; }
    public string CliExePath { get; }

    public string StdOut
    {
        get { lock (_gate) return _stdout.ToString(); }
    }

    public string StdErr
    {
        get { lock (_gate) return _stderr.ToString(); }
    }

    public CliSpawn()
    {
        CliDirectory = LocateCliDirectory();
        CliDllPath = Path.Combine(CliDirectory, "titanium.dll");
        if (!File.Exists(CliDllPath))
        {
            throw new FileNotFoundException(
                "titanium.dll not found. Build Titanium.Cli (Release) before CliQaProbe.",
                CliDllPath);
        }

        CliExePath = Path.Combine(
            CliDirectory,
            OperatingSystem.IsWindows() ? "titanium.exe" : "titanium");
        if (!File.Exists(CliExePath))
        {
            throw new FileNotFoundException(
                "CLI apphost not found. Build Titanium.Cli so service install records titanium (not dotnet).",
                CliExePath);
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

    public static (HttpListener Listener, int Port) BindHttpListenerOrRetry(Func<int, string> prefixFactory, int maxAttempts = 8)
    {
        Exception? last = null;
        for (var i = 0; i < maxAttempts; i++)
        {
            var port = GetFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add(prefixFactory(port));
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                try { listener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException($"Failed to bind HttpListener after {maxAttempts} attempts.", last);
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunOnceAsync(
        string[] args,
        TimeSpan? timeout = null,
        IDictionary<string, string?>? env = null)
    {
        using var process = StartProcess(CliExePath, args, env);
        return await WaitProcessAsync(process, timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Machine service commands: run via <c>sudo -n</c> when the probe is not already root.
    /// </summary>
    public async Task<(int ExitCode, string StdOut, string StdErr)> RunOnceSystemAsync(
        string[] args,
        TimeSpan? timeout = null)
    {
        if (Elevation.IsElevated())
            return await RunOnceAsync(args, timeout).ConfigureAwait(false);

        if (!File.Exists("/usr/bin/sudo"))
            return (1, "", "sudo not found");

        var sudoArgs = new List<string> { "-n", "--", CliExePath };
        sudoArgs.AddRange(args);
        using var process = StartProcess("/usr/bin/sudo", sudoArgs.ToArray(), env: null);
        return await WaitProcessAsync(process, timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>systemd --user</c> commands: always as the login user (drop privileges when root via sudo).
    /// </summary>
    public async Task<(int ExitCode, string StdOut, string StdErr)> RunOnceLoginUserAsync(
        string[] args,
        TimeSpan? timeout = null)
    {
        var user = Elevation.TryResolveLoginUser();
        var userEnv = Elevation.TryBuildLoginUserEnv();
        if (user is null || userEnv is null)
        {
            return (1, "",
                "No login user for systemd --user (run as a normal user, or sudo from a login user so SUDO_USER is set).");
        }

        if (!Elevation.IsElevated())
            return await RunOnceAsync(args, timeout, userEnv).ConfigureAwait(false);

        if (!File.Exists("/usr/bin/sudo"))
            return (1, "", "sudo not found");

        // sudo -n -u <user> env HOME=... XDG_RUNTIME_DIR=... <titanium> ...
        var sudoArgs = new List<string> { "-n", "-u", user, "--", "env" };
        foreach (var (k, v) in userEnv)
        {
            if (v is not null)
                sudoArgs.Add($"{k}={v}");
        }

        sudoArgs.Add(CliExePath);
        sudoArgs.AddRange(args);
        using var process = StartProcess("/usr/bin/sudo", sudoArgs.ToArray(), env: null);
        return await WaitProcessAsync(process, timeout).ConfigureAwait(false);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> WaitProcessAsync(
        Process process,
        TimeSpan? timeout)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException($"CLI timed out. stdout={StdOut} stderr={StdErr}");
        }

        return (process.ExitCode, StdOut, StdErr);
    }

    public async Task StartRunAsync(string configPath, bool verbose = false, IDictionary<string, string?>? env = null)
    {
        if (_runProcess is not null)
            throw new InvalidOperationException("Already started.");

        var args = new List<string> { "run", "-c", configPath };
        if (verbose)
            args.Add("-v");

        _runProcess = StartProcess(CliExePath, args.ToArray(), env);
        await WaitForOutputAsync("running", TimeSpan.FromSeconds(45)).ConfigureAwait(false);
    }

    public async Task WaitForOutputAsync(string substring, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (StdOut.Contains(substring, StringComparison.OrdinalIgnoreCase) ||
                StdErr.Contains(substring, StringComparison.OrdinalIgnoreCase))
                return;

            if (_runProcess is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"CLI exited early ({_runProcess.ExitCode}). stdout={StdOut} stderr={StdErr}");
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for '{substring}'. stdout={StdOut} stderr={StdErr}");
    }

    public bool TryEnsurePlusDll()
    {
        try
        {
            var plus = LocatePlusDll();
            var plusDir = Path.GetDirectoryName(plus)!;
            foreach (var file in Directory.EnumerateFiles(plusDir, "*.dll"))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("Titanium.Web.Proxy", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("Titanium.Plus.dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(CliDirectory, name), overwrite: true);
            }

            return File.Exists(Path.Combine(CliDirectory, "Titanium.Plus.dll"));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_runProcess is null)
            return;
        TryKill(_runProcess);
        _runProcess.Dispose();
        _runProcess = null;
    }

    private Process StartProcess(string fileName, string[] args, IDictionary<string, string?>? env)
    {
        lock (_gate)
        {
            _stdout.Clear();
            _stderr.Clear();
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = CliDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                if (v is null)
                    psi.Environment.Remove(k);
                else
                    psi.Environment[k] = v;
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

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Titanium.Web.Proxy.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Repo root not found from " + AppContext.BaseDirectory);
    }

    private static string LocateCliDirectory()
    {
        var repo = FindRepoRoot();
        foreach (var cfg in new[] { "Release", "Debug" })
        {
            var dir = Path.Combine(repo, "src", "Titanium.Cli", "bin", cfg, "net10.0");
            if (File.Exists(Path.Combine(dir, "titanium.dll")))
                return dir;
        }

        throw new DirectoryNotFoundException("Could not locate Titanium.Cli output. Build src/Titanium.Cli first.");
    }

    private static string LocatePlusDll()
    {
        var repo = FindRepoRoot();
        foreach (var cfg in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(repo, "src", "Titanium.Plus", "bin", cfg, "net10.0", "Titanium.Plus.dll");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("Titanium.Plus.dll not found.");
    }
}
