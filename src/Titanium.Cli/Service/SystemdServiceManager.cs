using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Titanium.Cli.Service;

[SupportedOSPlatform("linux")]
internal sealed class SystemdServiceManager : IOsServiceManager
{
    public async Task InstallAsync(ServiceInstallRequest request)
    {
        if (!request.User)
        {
            EnsureRoot();
        }

        var unitPath = ServiceUnitFactory.ResolveSystemdUnitPath(request.Name, request.User);
        var dir = Path.GetDirectoryName(unitPath)!;
        Directory.CreateDirectory(dir);

        var unit = ServiceUnitFactory.BuildSystemdUnit(
            request.ExePath, request.ConfigPath, request.WorkingDirectory, request.User);
        await File.WriteAllTextAsync(unitPath, unit, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .ConfigureAwait(false);
        AsyncConsole.WriteLine($"Wrote {unitPath}");

        await SystemctlAsync(request.User, "daemon-reload").ConfigureAwait(false);
        await SystemctlAsync(request.User, "enable", request.Name + ".service").ConfigureAwait(false);

        if (request.User)
        {
            AsyncConsole.WriteLine(
                "Note: for start-at-boot without an interactive login, run: loginctl enable-linger $USER");
        }

        if (request.StartAfterInstall)
        {
            await StartAsync(request.Name, request.User).ConfigureAwait(false);
        }
    }

    public async Task UninstallAsync(string name, bool user)
    {
        if (!user)
        {
            EnsureRoot();
        }

        var unit = name + ".service";
        try
        {
            await SystemctlAsync(user, "stop", unit).ConfigureAwait(false);
        }
        catch
        {
            // May already be stopped / missing.
        }

        try
        {
            await SystemctlAsync(user, "disable", unit).ConfigureAwait(false);
        }
        catch
        {
            // May not be enabled.
        }

        var unitPath = ServiceUnitFactory.ResolveSystemdUnitPath(name, user);
        if (File.Exists(unitPath))
        {
            File.Delete(unitPath);
            AsyncConsole.WriteLine($"Removed {unitPath}");
        }

        await SystemctlAsync(user, "daemon-reload").ConfigureAwait(false);
    }

    public async Task StartAsync(string name, bool user)
    {
        if (!user)
        {
            EnsureRoot();
        }

        await SystemctlAsync(user, "start", name + ".service").ConfigureAwait(false);
        AsyncConsole.WriteLine($"Service '{name}' started.");
    }

    public async Task StopAsync(string name, bool user)
    {
        if (!user)
        {
            EnsureRoot();
        }

        await SystemctlAsync(user, "stop", name + ".service").ConfigureAwait(false);
        AsyncConsole.WriteLine($"Service '{name}' stopped.");
    }

    public async Task RestartAsync(string name, bool user)
    {
        if (!user)
        {
            EnsureRoot();
        }

        await SystemctlAsync(user, "restart", name + ".service").ConfigureAwait(false);
        AsyncConsole.WriteLine($"Service '{name}' restarted.");
    }

    public async Task<ServiceStatusResult> StatusAsync(string name, bool user)
    {
        var unitPath = ServiceUnitFactory.ResolveSystemdUnitPath(name, user);
        if (!File.Exists(unitPath))
        {
            return new ServiceStatusResult(ServiceStatusKind.NotInstalled, name);
        }

        var (code, stdout) = await SystemctlCaptureAsync(user, "is-active", name + ".service")
            .ConfigureAwait(false);
        var state = stdout.Trim();
        var kind = state switch
        {
            "active" => ServiceStatusKind.Running,
            "inactive" or "failed" => ServiceStatusKind.Stopped,
            _ => ServiceStatusKind.Other,
        };
        return new ServiceStatusResult(kind, name, string.IsNullOrEmpty(state) ? $"exit {code}" : state);
    }

    private static void EnsureRoot()
    {
        if (!IsRoot())
        {
            throw new InvalidOperationException(
                "Root privileges required for a system service. Re-run with sudo (or use --user).");
        }
    }

    internal static bool IsRoot() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && GetEuid() == 0;

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint GetEuid();

    private static async Task SystemctlAsync(bool user, params string[] args)
    {
        var (code, stdout, stderr) = await RunSystemctlAsync(user, args).ConfigureAwait(false);
        if (code != 0)
        {
            var msg = (stdout + stderr).Trim();
            throw new InvalidOperationException(
                string.IsNullOrEmpty(msg)
                    ? $"systemctl exited with code {code}."
                    : msg);
        }
    }

    private static async Task<(int Code, string Stdout)> SystemctlCaptureAsync(bool user, params string[] args)
    {
        var (code, stdout, _) = await RunSystemctlAsync(user, args).ConfigureAwait(false);
        return (code, stdout);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunSystemctlAsync(
        bool user,
        params string[] args)
    {
        const string systemctl = "/usr/bin/systemctl";
        if (!File.Exists(systemctl))
        {
            throw new InvalidOperationException($"{systemctl} not found. systemd is required on Linux.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = systemctl,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (user)
        {
            psi.ArgumentList.Add("--user");
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start systemctl.");
        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return (proc.ExitCode, stdout, stderr);
    }
}
