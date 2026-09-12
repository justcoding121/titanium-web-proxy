using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Titanium.E2E.Tests.Harness;

/// <summary>Spawns the titanium CLI from build output and tears it down.</summary>
public sealed partial class CliProcessHarness : IDisposable
{
    private Process? _process;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _gate = new();
    private readonly bool _ownsIsolatedDirectory;
    private string? _isolatedDirectory;

    /// <summary>How the CLI process is launched.</summary>
    public enum SpawnMode
    {
        /// <summary><c>dotnet titanium.dll</c> — fine for run/test/help; not for service install binPath.</summary>
        DotnetDll,

        /// <summary>Apphost <c>titanium</c>/<c>titanium.exe</c> — required for OS service install.</summary>
        Apphost,
    }

    public string CliDirectory { get; private set; }
    public string CliDllPath { get; private set; }
    public string CliExePath { get; private set; }
    public SpawnMode Mode { get; }

    public string StdOut
    {
        get { lock (_gate) return _stdout.ToString(); }
    }

    public string StdErr
    {
        get { lock (_gate) return _stderr.ToString(); }
    }

    public int? ExitCode => _process is { HasExited: true } ? _process.ExitCode : null;

    /// <summary>PID of a long-running <c>run</c> process started via <see cref="StartRunAsync"/>.</summary>
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : _process?.Id;

    public CliProcessHarness(SpawnMode mode = SpawnMode.DotnetDll)
    {
        Mode = mode;
        CliDirectory = LocateCliDirectory();
        CliDllPath = Path.Combine(CliDirectory, "titanium.dll");
        if (!File.Exists(CliDllPath))
        {
            throw new FileNotFoundException(
                "titanium.dll not found. Build Titanium.Cli (Release/Debug) before E2E tests.",
                CliDllPath);
        }

        CliExePath = Path.Combine(
            CliDirectory,
            OperatingSystem.IsWindows() ? "titanium.exe" : "titanium");
        if (mode == SpawnMode.Apphost && !File.Exists(CliExePath))
        {
            throw new FileNotFoundException(
                "CLI apphost not found. Build Titanium.Cli so service install records titanium (not dotnet).",
                CliExePath);
        }

        _ownsIsolatedDirectory = false;
    }

    private CliProcessHarness(string isolatedDir, SpawnMode mode)
    {
        Mode = mode;
        _ownsIsolatedDirectory = true;
        _isolatedDirectory = isolatedDir;
        CliDirectory = isolatedDir;
        CliDllPath = Path.Combine(isolatedDir, "titanium.dll");
        CliExePath = Path.Combine(
            isolatedDir,
            OperatingSystem.IsWindows() ? "titanium.exe" : "titanium");
        if (!File.Exists(CliDllPath))
        {
            throw new FileNotFoundException("Isolated CLI copy missing titanium.dll.", CliDllPath);
        }

        if (mode == SpawnMode.Apphost && !File.Exists(CliExePath))
        {
            throw new FileNotFoundException("Isolated CLI copy missing apphost.", CliExePath);
        }
    }

    /// <summary>
    /// Copy the CLI build output into a temp directory so <c>update</c> apply scripts
    /// cannot overwrite the solution build tree.
    /// </summary>
    public static CliProcessHarness CreateIsolatedCopy(
        SpawnMode mode = SpawnMode.Apphost,
        bool copyPlus = false)
    {
        var source = LocateCliDirectory();
        var dest = Path.Combine(Path.GetTempPath(), "twp-cli-iso-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(source, dest);
        if (!OperatingSystem.IsWindows())
        {
            TryChmodExecutable(Path.Combine(dest, "titanium"));
            TryChmodExecutable(Path.Combine(dest, "twp"));
        }

        var harness = new CliProcessHarness(dest, mode);
        if (copyPlus)
        {
            harness.EnsurePlusDllBesideCli(copy: true);
        }
        else
        {
            harness.EnsurePlusDllBesideCli(copy: false);
        }

        return harness;
    }

    public static string NewServiceName() =>
        "titanium-e2e-" + Guid.NewGuid().ToString("N")[..12];

    public static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        try
        {
            return NativeGetEuid() == 0;
        }
        catch
        {
            return false;
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

    /// <summary>
    /// Bind an <see cref="HttpListener"/> with retries against Windows TOCTOU / excluded-port races
    /// after <see cref="GetFreePort"/>.
    /// </summary>
    public static (HttpListener Listener, int Port) BindHttpListenerOrRetry(
        Func<int, string> prefixFactory,
        int maxAttempts = 8)
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
                try
                {
                    listener.Close();
                }
                catch
                {
                    // ignore
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to bind HttpListener after {maxAttempts} attempts.", last);
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

        var plusDir = Path.GetDirectoryName(LocatePlusDll())!;
        foreach (var file in Directory.EnumerateFiles(plusDir, "*.dll"))
        {
            var name = Path.GetFileName(file);
            // Skip Core/Abstractions already provided by the CLI host.
            if (name.StartsWith("Titanium.Web.Proxy", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Titanium.Plus.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(CliDirectory, name), overwrite: true);
        }
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunOnceAsync(
        string[] args,
        TimeSpan? timeout = null,
        IDictionary<string, string?>? env = null)
    {
        using var process = StartProcess(ResolveFileName(), BuildArgList(args), env);
        return await WaitProcessAsync(process, timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Machine service commands: run via <c>sudo -n</c> when the process is not already root (Unix).
    /// On Windows, runs as the current user (CI runners are typically already admin).
    /// </summary>
    public async Task<(int ExitCode, string StdOut, string StdErr)> RunOnceSystemAsync(
        string[] args,
        TimeSpan? timeout = null,
        IDictionary<string, string?>? env = null)
    {
        if (OperatingSystem.IsWindows() || IsElevated())
        {
            return await RunOnceAsync(args, timeout, env).ConfigureAwait(false);
        }

        if (!File.Exists("/usr/bin/sudo"))
        {
            return (1, "", "sudo not found");
        }

        EnsureApphost();
        var sudoArgs = new List<string> { "-n", "--", CliExePath };
        sudoArgs.AddRange(args);
        using var process = StartProcess("/usr/bin/sudo", sudoArgs.ToArray(), env);
        return await WaitProcessAsync(process, timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>systemd --user</c> / LaunchAgent commands as the login user.
    /// </summary>
    public async Task<(int ExitCode, string StdOut, string StdErr)> RunOnceLoginUserAsync(
        string[] args,
        TimeSpan? timeout = null)
    {
        var userEnv = TryBuildLoginUserEnv();
        if (userEnv is null)
        {
            return (1, "",
                "No login user for --user services (run as a normal user, or sudo so SUDO_USER is set).");
        }

        var user = userEnv["USER"];
        if (!IsElevated())
        {
            return await RunOnceAsync(args, timeout, userEnv).ConfigureAwait(false);
        }

        if (!File.Exists("/usr/bin/sudo") || string.IsNullOrEmpty(user))
        {
            return (1, "", "sudo not found or no login user");
        }

        EnsureApphost();
        var sudoArgs = new List<string> { "-n", "-u", user!, "--", "env" };
        foreach (var (k, v) in userEnv)
        {
            if (v is not null)
            {
                sudoArgs.Add($"{k}={v}");
            }
        }

        sudoArgs.Add(CliExePath);
        sudoArgs.AddRange(args);
        using var process = StartProcess("/usr/bin/sudo", sudoArgs.ToArray(), env: null);
        return await WaitProcessAsync(process, timeout).ConfigureAwait(false);
    }

    public async Task StartRunAsync(
        string configPath,
        IDictionary<string, string?>? env = null,
        bool verbose = false,
        bool serviceMode = false,
        string? serviceName = null)
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

        if (serviceMode)
        {
            args.Add("--service");
            if (!string.IsNullOrEmpty(serviceName))
            {
                args.Add("--name");
                args.Add(serviceName);
            }
        }

        _process = StartProcess(ResolveFileName(), BuildArgList(args.ToArray()), env);
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

    /// <summary>Sends SIGHUP to the running CLI (Unix). Throws on Windows.</summary>
    public void SendSighup()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SIGHUP is not available on Windows.");
        }

        if (_process is null || _process.HasExited)
        {
            throw new InvalidOperationException("CLI process is not running.");
        }

        // SIGHUP = 1 on Linux/macOS
        if (NativeKill(_process.Id, 1) != 0)
        {
            throw new InvalidOperationException($"kill(SIGHUP) failed for pid {_process.Id} (errno may be set).");
        }
    }

    /// <summary>Sends SIGTERM to the running CLI (Unix) or kills the tree (Windows).</summary>
    public void SendSigterm()
    {
        if (_process is null || _process.HasExited)
        {
            throw new InvalidOperationException("CLI process is not running.");
        }

        if (OperatingSystem.IsWindows())
        {
            TryKill(_process);
            return;
        }

        // SIGTERM = 15
        if (NativeKill(_process.Id, 15) != 0)
        {
            TryKill(_process);
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int NativeKill(int pid, int sig);

    [LibraryImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static partial uint NativeGetEuid();

    public void Dispose()
    {
        if (_process is not null)
        {
            TryKill(_process);
            _process.Dispose();
            _process = null;
        }

        if (_ownsIsolatedDirectory && _isolatedDirectory is not null)
        {
            try
            {
                if (Directory.Exists(_isolatedDirectory))
                {
                    Directory.Delete(_isolatedDirectory, recursive: true);
                }
            }
            catch
            {
                // ignore locked files after update apply
            }

            _isolatedDirectory = null;
        }
    }

    private void EnsureApphost()
    {
        if (Mode != SpawnMode.Apphost)
        {
            throw new InvalidOperationException(
                "Apphost spawn mode is required for system/service elevation helpers.");
        }

        if (!File.Exists(CliExePath))
        {
            throw new FileNotFoundException("CLI apphost missing.", CliExePath);
        }
    }

    private string ResolveFileName() =>
        Mode == SpawnMode.Apphost ? CliExePath : "dotnet";

    private string[] BuildArgList(string[] args)
    {
        if (Mode == SpawnMode.Apphost)
        {
            return args;
        }

        var list = new string[args.Length + 1];
        list[0] = CliDllPath;
        Array.Copy(args, 0, list, 1, args.Length);
        return list;
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

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            // Skip huge/irrelevant folders if present
            if (name is "ref" or "refs")
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(dest, name));
        }
    }

    private static void TryChmodExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                ArgumentList = { "+x", path },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(5000);
        }
        catch
        {
            // ignore
        }
    }

    private static Dictionary<string, string?>? TryBuildLoginUserEnv()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var user = TryResolveLoginUser();
        if (user is null)
        {
            return null;
        }

        var uid = TryResolveLoginUid(user);
        var home = TryResolveLoginHome(user);
        if (uid is null || home is null)
        {
            return null;
        }

        var runtime = $"/run/user/{uid.Value}";
        return new Dictionary<string, string?>
        {
            ["HOME"] = home,
            ["USER"] = user,
            ["LOGNAME"] = user,
            ["XDG_RUNTIME_DIR"] = runtime,
            ["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={runtime}/bus",
            ["TITANIUM_NO_ELEVATE"] = "1",
        };
    }

    private static string? TryResolveLoginUser()
    {
        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        if (!string.IsNullOrWhiteSpace(sudoUser) &&
            !sudoUser.Equals("root", StringComparison.Ordinal))
        {
            return sudoUser;
        }

        if (!IsElevated())
        {
            var name = Environment.UserName;
            return string.IsNullOrWhiteSpace(name) || name.Equals("root", StringComparison.Ordinal)
                ? null
                : name;
        }

        return null;
    }

    private static uint? TryResolveLoginUid(string user)
    {
        var sudoUid = Environment.GetEnvironmentVariable("SUDO_UID");
        if (!string.IsNullOrEmpty(sudoUid) &&
            string.Equals(Environment.GetEnvironmentVariable("SUDO_USER"), user, StringComparison.Ordinal) &&
            uint.TryParse(sudoUid, out var parsed))
        {
            return parsed;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "id",
                Arguments = "-u " + user,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return uint.TryParse(text, out var uid) ? uid : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveLoginHome(string user)
    {
        var sudoHome = Environment.GetEnvironmentVariable("SUDO_HOME");
        if (!string.IsNullOrEmpty(sudoHome) &&
            string.Equals(Environment.GetEnvironmentVariable("SUDO_USER"), user, StringComparison.Ordinal) &&
            Directory.Exists(sudoHome))
        {
            return sudoHome;
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine("/home", user),
                     Path.Combine("/Users", user),
                 })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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

    internal static string LocatePlusDll()
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
