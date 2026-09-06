using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Titanium.Cli.Service;

[SupportedOSPlatform("macos")]
internal sealed class LaunchdServiceManager : IOsServiceManager
{
    public async Task InstallAsync(ServiceInstallRequest request)
    {
        if (!request.User)
        {
            EnsureRoot();
        }

        var label = ServiceDefaults.ResolveMacOsLabel(request.Name);
        var userHome = request.User ? ResolveUserHome() : null;
        var logDir = ServiceUnitFactory.ResolveLaunchdLogDirectory(request.User, userHome);
        Directory.CreateDirectory(logDir);
        var outPath = Path.Combine(logDir, request.Name + ".out.log");
        var errPath = Path.Combine(logDir, request.Name + ".err.log");

        var programPrefix = request.ProgramPrefix;
        if (!request.User)
        {
            var sourceDir = ServicePayload.DiscoverAppDirectory(programPrefix);
            if (!string.IsNullOrEmpty(sourceDir) && Directory.Exists(sourceDir))
            {
                var destDir = ServicePayload.MacOsDaemonPayloadDirectory(request.Name);
                ServicePayload.CopyDirectory(sourceDir, destDir);
                programPrefix = ServicePayload.RemapPrefix(programPrefix, sourceDir, destDir);
                TryChmodExecute(programPrefix[0]);
            }
        }

        var plist = ServiceUnitFactory.BuildLaunchdPlist(
            label,
            programPrefix,
            request.ConfigPath,
            request.WorkingDirectory,
            outPath,
            errPath,
            request.EnvironmentVariables);

        var plistPath = ServiceUnitFactory.ResolveLaunchdPlistPath(label, request.User, userHome);
        var dir = Path.GetDirectoryName(plistPath)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(plistPath, plist, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .ConfigureAwait(false);
        AsyncConsole.WriteLine($"Wrote {plistPath}");

        var domain = ResolveDomain(request.User);
        // bootout first so reinstall is idempotent.
        await TryBootoutAsync(domain, plistPath, label).ConfigureAwait(false);

        if (!request.StartAfterInstall)
        {
            // Leave the plist on disk for boot/login; do not bootstrap now.
            // bootstrap + RunAtLoad would start immediately and ignore --no-start.
            return;
        }

        await LaunchctlAsync("bootstrap", domain, plistPath).ConfigureAwait(false);
        await LaunchctlAsync("kickstart", "-k", domain + "/" + label).ConfigureAwait(false);
        AsyncConsole.WriteLine($"Service '{label}' started.");
    }

    public async Task UninstallAsync(string name, bool user)
    {
        if (!user)
        {
            EnsureRoot();
        }

        var label = ServiceDefaults.ResolveMacOsLabel(name);
        var userHome = user ? ResolveUserHome() : null;
        var plistPath = ServiceUnitFactory.ResolveLaunchdPlistPath(label, user, userHome);
        var domain = ResolveDomain(user);
        await TryBootoutAsync(domain, plistPath, label).ConfigureAwait(false);

        if (File.Exists(plistPath))
        {
            File.Delete(plistPath);
            AsyncConsole.WriteLine($"Removed {plistPath}");
        }

        if (!user)
            ServicePayload.TryDeleteDirectory(ServicePayload.MacOsDaemonPayloadDirectory(name));
    }

    public async Task StartAsync(string name, bool user)
    {
        if (!user)
        {
            EnsureRoot();
        }

        var label = ServiceDefaults.ResolveMacOsLabel(name);
        var userHome = user ? ResolveUserHome() : null;
        var plistPath = ServiceUnitFactory.ResolveLaunchdPlistPath(label, user, userHome);
        if (!File.Exists(plistPath))
        {
            throw new InvalidOperationException($"Service '{label}' is not installed.");
        }

        var domain = ResolveDomain(user);
        if (!await IsLoadedAsync(domain, label).ConfigureAwait(false))
        {
            await LaunchctlAsync("bootstrap", domain, plistPath).ConfigureAwait(false);
        }

        await LaunchctlAsync("kickstart", "-k", domain + "/" + label).ConfigureAwait(false);
        AsyncConsole.WriteLine($"Service '{label}' started.");
    }

    public async Task StopAsync(string name, bool user)
    {
        if (!user)
        {
            EnsureRoot();
        }

        var label = ServiceDefaults.ResolveMacOsLabel(name);
        var userHome = user ? ResolveUserHome() : null;
        var plistPath = ServiceUnitFactory.ResolveLaunchdPlistPath(label, user, userHome);
        var domain = ResolveDomain(user);
        // KeepAlive=true restarts after SIGTERM. bootout unloads the job and leaves the plist.
        await TryBootoutAsync(domain, plistPath, label).ConfigureAwait(false);
        AsyncConsole.WriteLine($"Service '{label}' stopped.");
    }

    public async Task RestartAsync(string name, bool user)
    {
        await StopAsync(name, user).ConfigureAwait(false);
        await StartAsync(name, user).ConfigureAwait(false);
    }

    public async Task<ServiceStatusResult> StatusAsync(string name, bool user)
    {
        var label = ServiceDefaults.ResolveMacOsLabel(name);
        var userHome = user ? ResolveUserHome() : null;
        var plistPath = ServiceUnitFactory.ResolveLaunchdPlistPath(label, user, userHome);
        if (!File.Exists(plistPath))
        {
            return new ServiceStatusResult(ServiceStatusKind.NotInstalled, label);
        }

        var domain = ResolveDomain(user);
        var (code, stdout, stderr) = await RunLaunchctlAsync("print", domain + "/" + label)
            .ConfigureAwait(false);
        var text = stdout + stderr;
        if (code != 0 && text.Contains("Could not find", StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceStatusResult(ServiceStatusKind.Stopped, label, "not loaded");
        }

        // print output includes "state = running" when active.
        if (text.Contains("state = running", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("\"PID\" =", StringComparison.Ordinal))
        {
            return new ServiceStatusResult(ServiceStatusKind.Running, label, "running");
        }

        return new ServiceStatusResult(ServiceStatusKind.Stopped, label, "loaded");
    }

    private static string ResolveDomain(bool user)
    {
        if (!user)
            return "system";

        var uid = ResolveTargetUid();
        if (uid == 0)
        {
            throw new InvalidOperationException(
                "Cannot install a LaunchAgent as root (gui/0). Run without sudo, or invoke sudo from a logged-in user so SUDO_UID is set.");
        }

        return $"gui/{uid}";
    }

    internal static uint ResolveTargetUid()
    {
        if (GetEuid() == 0)
        {
            var sudoUid = Environment.GetEnvironmentVariable("SUDO_UID");
            if (uint.TryParse(sudoUid, out var uid) && uid != 0)
                return uid;
        }

        return GetUid();
    }

    internal static string ResolveUserHome()
    {
        if (GetEuid() == 0)
        {
            var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
            if (!string.IsNullOrEmpty(sudoUser))
            {
                foreach (var home in new[] { "/Users/" + sudoUser, "/home/" + sudoUser })
                {
                    if (Directory.Exists(home))
                        return home;
                }
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static void TryChmodExecute(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Best-effort; launchd will fail clearly if the binary is not executable.
        }
    }

    private static void EnsureRoot()
    {
        if (GetEuid() != 0)
        {
            throw new InvalidOperationException(
                "Root privileges required for a LaunchDaemon. Re-run with sudo (or use --user for a LaunchAgent).");
        }
    }

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "getuid", SetLastError = true)]
    private static extern uint GetUid();

    private static async Task<bool> IsLoadedAsync(string domain, string label)
    {
        var (code, stdout, stderr) = await RunLaunchctlAsync("print", domain + "/" + label)
            .ConfigureAwait(false);
        if (code == 0)
            return true;
        var text = stdout + stderr;
        return !text.Contains("Could not find", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task TryBootoutAsync(string domain, string plistPath, string label)
    {
        try
        {
            await LaunchctlAsync("bootout", domain, plistPath).ConfigureAwait(false);
            return;
        }
        catch
        {
            // Not loaded, or launchctl wants domain/label.
        }

        try
        {
            await LaunchctlAsync("bootout", domain + "/" + label).ConfigureAwait(false);
        }
        catch
        {
            // Already unloaded.
        }
    }

    private static async Task LaunchctlAsync(params string[] args)
    {
        var (code, stdout, stderr) = await RunLaunchctlAsync(args).ConfigureAwait(false);
        if (code != 0)
        {
            var msg = (stdout + stderr).Trim();
            throw new InvalidOperationException(
                string.IsNullOrEmpty(msg)
                    ? $"launchctl exited with code {code}."
                    : msg);
        }
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunLaunchctlAsync(
        params string[] args)
    {
        const string launchctl = "/bin/launchctl";
        if (!File.Exists(launchctl))
        {
            throw new InvalidOperationException($"{launchctl} not found.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = launchctl,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start launchctl.");
        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
    }
}
