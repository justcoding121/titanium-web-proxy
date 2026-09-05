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
        var logDir = ServiceUnitFactory.ResolveLaunchdLogDirectory(request.User);
        Directory.CreateDirectory(logDir);
        var outPath = Path.Combine(logDir, request.Name + ".out.log");
        var errPath = Path.Combine(logDir, request.Name + ".err.log");

        var plist = ServiceUnitFactory.BuildLaunchdPlist(
            label,
            request.ExePath,
            request.ConfigPath,
            request.WorkingDirectory,
            outPath,
            errPath);

        var plistPath = ServiceUnitFactory.ResolveLaunchdPlistPath(label, request.User);
        var dir = Path.GetDirectoryName(plistPath)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(plistPath, plist, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .ConfigureAwait(false);
        AsyncConsole.WriteLine($"Wrote {plistPath}");

        var domain = ResolveDomain(request.User);
        // bootout first so reinstall is idempotent.
        try
        {
            await LaunchctlAsync("bootout", domain, plistPath).ConfigureAwait(false);
        }
        catch
        {
            // Not loaded yet.
        }

        await LaunchctlAsync("bootstrap", domain, plistPath).ConfigureAwait(false);

        if (request.StartAfterInstall)
        {
            await LaunchctlAsync("kickstart", "-k", domain + "/" + label).ConfigureAwait(false);
            AsyncConsole.WriteLine($"Service '{label}' started.");
        }
    }

    public async Task UninstallAsync(string name, bool user)
    {
        if (!user)
        {
            EnsureRoot();
        }

        var label = ServiceDefaults.ResolveMacOsLabel(name);
        var plistPath = ServiceUnitFactory.ResolveLaunchdPlistPath(label, user);
        var domain = ResolveDomain(user);
        try
        {
            await LaunchctlAsync("bootout", domain, plistPath).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await LaunchctlAsync("bootout", domain + "/" + label).ConfigureAwait(false);
            }
            catch
            {
                // Already unloaded.
            }
        }

        if (File.Exists(plistPath))
        {
            File.Delete(plistPath);
            AsyncConsole.WriteLine($"Removed {plistPath}");
        }
    }

    public async Task StartAsync(string name, bool user)
    {
        if (!user)
        {
            EnsureRoot();
        }

        var label = ServiceDefaults.ResolveMacOsLabel(name);
        var domain = ResolveDomain(user);
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
        var domain = ResolveDomain(user);
        await LaunchctlAsync("kill", "SIGTERM", domain + "/" + label).ConfigureAwait(false);
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
        var plistPath = ServiceUnitFactory.ResolveLaunchdPlistPath(label, user);
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
        if (user)
        {
            return $"gui/{GetUid()}";
        }

        return "system";
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
