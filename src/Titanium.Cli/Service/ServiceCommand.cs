using Titanium.Cli.Config;
using Titanium.Cli.Parsers;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Cli.Service;

internal static class ServiceCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        // args[0] == "service"
        if (args.Length < 2 || CliHelp.IsHelpToken(args[1]))
        {
            return PrintHelp();
        }

        var sub = args[1].ToLowerInvariant();
        var rest = args.AsSpan(2);
        if (CliHelp.RequestsHelp(rest))
        {
            return PrintSubHelp(sub);
        }

        try
        {
            return sub switch
            {
                "install" => await InstallAsync(args).ConfigureAwait(false),
                "uninstall" => await UninstallAsync(args).ConfigureAwait(false),
                "start" => await StartAsync(args).ConfigureAwait(false),
                "stop" => await StopAsync(args).ConfigureAwait(false),
                "restart" => await RestartAsync(args).ConfigureAwait(false),
                "status" => await StatusAsync(args).ConfigureAwait(false),
                "help" or "-h" or "--help" => PrintHelp(),
                _ => Unknown(sub),
            };
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError(ex.Message);
            return 1;
        }
    }

    internal static int PrintHelp()
    {
        AsyncConsole.WriteLine("""
            titanium service install|uninstall|start|stop|restart|status

              install    Register the proxy as an OS service (starts at boot).
              uninstall  Remove the OS service registration.
              start      Start the service.
              stop       Stop the service.
              restart    Stop then start.
              status     Print installed / running / stopped.

            Flags (most subcommands):
              --name <name>   Service name (default: titanium). macOS label becomes
                              com.justcoding121.<name> unless name already starts with com.
              --user          Per-user service (systemd --user / LaunchAgent). No root;
                              ports 80/443 usually fail. Default is a machine service.

            install also requires:
              -c, --config <path>   Config file used by the service.
              --no-start            Install and enable, but do not start immediately.

            Machine services need Administrator (Windows) or sudo (Linux/macOS).
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }

    internal static int PrintSubHelp(string sub) =>
        sub.ToLowerInvariant() switch
        {
            "install" => PrintInstallHelp(),
            "uninstall" => PrintSimpleHelp("uninstall", "Remove the OS service."),
            "start" => PrintSimpleHelp("start", "Start the OS service."),
            "stop" => PrintSimpleHelp("stop", "Stop the OS service."),
            "restart" => PrintSimpleHelp("restart", "Restart the OS service."),
            "status" => PrintSimpleHelp("status", "Show whether the service is installed and running."),
            _ => Unknown(sub),
        };

    private static int PrintInstallHelp()
    {
        AsyncConsole.WriteLine("""
            titanium service install -c <config> [--name titanium] [--user] [--no-start]

              -c, --config   Config path (validated before install; stored as an absolute path).
              --name         Service / unit name (default: titanium).
              --user         Per-user systemd unit or LaunchAgent (no elevation).
              --no-start     Do not start immediately after install.

            The unit runs: titanium run -c <abs-config> --service
            Working directory is the config file's directory.
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }

    private static int PrintSimpleHelp(string sub, string purpose)
    {
        AsyncConsole.WriteLine($"""
            titanium service {sub} [--name titanium] [--user]

              {purpose}
              --name   Service name (default: titanium).
              --user   Target the per-user service instead of the machine service.
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }

    private static int Unknown(string sub)
    {
        AsyncConsole.WriteError($"Unknown service subcommand: {sub}");
        PrintHelp();
        return 1;
    }

    private static async Task<int> InstallAsync(string[] args)
    {
        var configPath = ParseConfigPathRequired(args);
        var name = ParseName(args);
        var user = ParseUser(args);
        var noStart = args.Contains("--no-start", StringComparer.OrdinalIgnoreCase);

        if (user && OperatingSystem.IsWindows())
        {
            throw new ArgumentException(
                "--user is not supported on Windows (machine Windows Service only). Omit --user.");
        }

        // Validate config before writing any unit.
        var loaded = ConfigLoader.Load(configPath);
        var errors = TwpConfigValidator.Validate(loaded.Config);
        if (errors.Count > 0)
        {
            foreach (var e in errors)
            {
                AsyncConsole.WriteError(e);
            }

            return 1;
        }

        var absConfig = Path.GetFullPath(configPath);
        var workDir = Path.GetDirectoryName(absConfig)
                      ?? throw new InvalidOperationException("Unable to resolve config directory.");
        var exe = ServiceDefaults.ResolveExePath();
        var manager = CreateManager();
        await manager.InstallAsync(new ServiceInstallRequest(
            name,
            absConfig,
            user,
            StartAfterInstall: !noStart,
            exe,
            workDir)).ConfigureAwait(false);
        AsyncConsole.WriteLine($"Service '{name}' installed.");
        return 0;
    }

    private static async Task<int> UninstallAsync(string[] args)
    {
        var manager = CreateManager();
        await manager.UninstallAsync(ParseName(args), ParseUser(args)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> StartAsync(string[] args)
    {
        var manager = CreateManager();
        await manager.StartAsync(ParseName(args), ParseUser(args)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> StopAsync(string[] args)
    {
        var manager = CreateManager();
        await manager.StopAsync(ParseName(args), ParseUser(args)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RestartAsync(string[] args)
    {
        var manager = CreateManager();
        await manager.RestartAsync(ParseName(args), ParseUser(args)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> StatusAsync(string[] args)
    {
        var name = ParseName(args);
        var user = ParseUser(args);
        var manager = CreateManager();
        var status = await manager.StatusAsync(name, user).ConfigureAwait(false);
        var label = status.Kind switch
        {
            ServiceStatusKind.NotInstalled => "not installed",
            ServiceStatusKind.Running => "running",
            ServiceStatusKind.Stopped => "stopped",
            _ => status.Detail ?? "unknown",
        };
        AsyncConsole.WriteLine($"Service '{status.Name}': {label}");
        return status.Kind == ServiceStatusKind.NotInstalled ? 1 : 0;
    }

    internal static IOsServiceManager CreateManager()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsServiceManager();
        }

        if (OperatingSystem.IsLinux())
        {
            return new SystemdServiceManager();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new LaunchdServiceManager();
        }

        throw new PlatformNotSupportedException(
            "OS service install is supported on Windows, Linux (systemd), and macOS (launchd) only.");
    }

    /// <summary>Best-effort check used by <c>titanium update</c> to warn about a locked exe.</summary>
    internal static async Task<bool> IsDefaultServiceRunningAsync()
    {
        try
        {
            var manager = CreateManager();
            var status = await manager.StatusAsync(ServiceDefaults.DefaultServiceName, user: false)
                .ConfigureAwait(false);
            if (status.Kind == ServiceStatusKind.Running)
            {
                return true;
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var userStatus = await manager.StatusAsync(ServiceDefaults.DefaultServiceName, user: true)
                    .ConfigureAwait(false);
                return userStatus.Kind == ServiceStatusKind.Running;
            }
        }
        catch
        {
            // Ignore probe failures during update.
        }

        return false;
    }

    internal static string ParseName(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--name" && i + 1 < args.Length)
            {
                var n = args[i + 1].Trim();
                if (string.IsNullOrEmpty(n))
                {
                    throw new ArgumentException("--name requires a non-empty value.");
                }

                return n;
            }
        }

        return ServiceDefaults.DefaultServiceName;
    }

    internal static bool ParseUser(string[] args) =>
        args.Contains("--user", StringComparer.OrdinalIgnoreCase);

    private static string ParseConfigPathRequired(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] is "-c" or "--config") && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        throw new ArgumentException("Missing required -c <config-path> for service install.");
    }
}
