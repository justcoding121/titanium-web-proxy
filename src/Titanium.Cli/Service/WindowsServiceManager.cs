using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Titanium.Cli.Service;

[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceManager : IOsServiceManager
{
    public async Task InstallAsync(ServiceInstallRequest request)
    {
        EnsureElevated();
        var binPath = ServiceUnitFactory.BuildWindowsBinPath(
            request.ExePath, request.ConfigPath, request.Name);

        // Delete existing if present so reinstall is idempotent.
        if (ServiceExists(request.Name))
        {
            await StopQuietAsync(request.Name).ConfigureAwait(false);
            await RunScAsync("delete", request.Name).ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);
        }

        await RunScAsync(
            "create",
            request.Name,
            "binPath=", binPath,
            "start=", "auto",
            "DisplayName=", ServiceDefaults.DisplayName).ConfigureAwait(false);

        await RunScAsync(
            "description",
            request.Name,
            ServiceDefaults.Description).ConfigureAwait(false);

        // Restart on failure after 5 seconds (reset period 60s).
        await RunScAsync(
            "failure",
            request.Name,
            "reset=", "60",
            "actions=", "restart/5000/restart/5000/restart/5000").ConfigureAwait(false);

        if (request.StartAfterInstall)
        {
            await StartAsync(request.Name, user: false).ConfigureAwait(false);
        }
    }

    public async Task UninstallAsync(string name, bool user)
    {
        _ = user;
        EnsureElevated();
        if (!ServiceExists(name))
        {
            AsyncConsole.WriteLine($"Service '{name}' is not installed.");
            return;
        }

        await StopQuietAsync(name).ConfigureAwait(false);
        await RunScAsync("delete", name).ConfigureAwait(false);
        AsyncConsole.WriteLine($"Service '{name}' removed.");
    }

    public Task StartAsync(string name, bool user)
    {
        _ = user;
        EnsureElevated();
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            AsyncConsole.WriteLine($"Service '{name}' is already running.");
            return Task.CompletedTask;
        }

        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
        AsyncConsole.WriteLine($"Service '{name}' started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(string name, bool user)
    {
        _ = user;
        EnsureElevated();
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            AsyncConsole.WriteLine($"Service '{name}' is already stopped.");
            return Task.CompletedTask;
        }

        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        AsyncConsole.WriteLine($"Service '{name}' stopped.");
        return Task.CompletedTask;
    }

    public async Task RestartAsync(string name, bool user)
    {
        await StopAsync(name, user).ConfigureAwait(false);
        await StartAsync(name, user).ConfigureAwait(false);
    }

    public Task<ServiceStatusResult> StatusAsync(string name, bool user)
    {
        _ = user;
        if (!ServiceExists(name))
        {
            return Task.FromResult(new ServiceStatusResult(ServiceStatusKind.NotInstalled, name));
        }

        using var sc = new ServiceController(name);
        var kind = sc.Status switch
        {
            ServiceControllerStatus.Running => ServiceStatusKind.Running,
            ServiceControllerStatus.Stopped => ServiceStatusKind.Stopped,
            _ => ServiceStatusKind.Other,
        };
        return Task.FromResult(new ServiceStatusResult(kind, name, sc.Status.ToString()));
    }

    private static bool ServiceExists(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            _ = sc.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task StopQuietAsync(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
        }
        catch
        {
            // Best-effort before delete.
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void EnsureElevated()
    {
        if (!IsElevated())
        {
            throw new InvalidOperationException(
                "Administrator privileges required. Re-run from an elevated prompt (Run as Administrator).");
        }
    }

    internal static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static async Task RunScAsync(params string[] args)
    {
        var scPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "sc.exe");
        if (!File.Exists(scPath))
        {
            throw new InvalidOperationException($"sc.exe not found at {scPath}.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = scPath,
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
            ?? throw new InvalidOperationException("Failed to start sc.exe.");
        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var msg = (stdout + stderr).Trim();
            throw new InvalidOperationException(
                string.IsNullOrEmpty(msg)
                    ? $"sc.exe exited with code {proc.ExitCode}."
                    : msg);
        }
    }
}
