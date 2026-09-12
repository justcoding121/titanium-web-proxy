namespace Titanium.Cli.Config;

/// <summary><c>titanium reload</c> — live-apply routes/clusters from the config file.</summary>
internal static class ReloadCommand
{
    public static Task<int> ExecuteAsync(string[] args)
    {
        if (CliHelp.RequestsHelp(args.AsSpan(1)))
            return Task.FromResult(PrintHelp());

        try
        {
            var configPath = TryParseConfigPath(args);
            var pidOverride = TryParsePid(args);

            if (configPath is null && pidOverride is null)
            {
                throw new ArgumentException(
                    "Provide -c <config> (preferred) or --pid <pid> (Unix only).");
            }

            if (OperatingSystem.IsWindows())
            {
                if (configPath is null)
                {
                    throw new ArgumentException(
                        "On Windows, titanium reload requires -c <config> " +
                        "(matches the named reload event of the running process).");
                }

                if (!ConfigReloadGate.TrySignalWindowsReload(configPath))
                {
                    throw new InvalidOperationException(
                        $"No running titanium process is waiting to reload config '{configPath}'. " +
                        "Start with `titanium run -c …` first.");
                }

                var pid = ConfigReloadGate.TryReadPid(configPath);
                AsyncConsole.WriteLine(pid is int p
                    ? $"Reload signaled (Windows event) for pid {p}."
                    : "Reload signaled (Windows event).");
                return Task.FromResult(0);
            }

            // Unix: SIGHUP via pid file or --pid.
            var pidUnix = pidOverride ?? (configPath is null ? null : ConfigReloadGate.TryReadPid(configPath));
            if (pidUnix is null)
            {
                throw new InvalidOperationException(
                    configPath is null
                        ? "Could not resolve a process id."
                        : $"No running titanium process found for config '{configPath}'. " +
                          "Start with `titanium run -c …` first, or pass --pid.");
            }

            if (!ConfigReloadGate.IsProcessAlive(pidUnix.Value))
            {
                throw new InvalidOperationException($"Process {pidUnix.Value} is not running.");
            }

            if (!ConfigReloadGate.TrySendSighup(pidUnix.Value))
            {
                throw new InvalidOperationException($"kill(SIGHUP) failed for pid {pidUnix.Value}.");
            }

            AsyncConsole.WriteLine($"Reload signaled (SIGHUP) to pid {pidUnix.Value}.");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError(ex.Message);
            return Task.FromResult(1);
        }
    }

    internal static int PrintHelp()
    {
        AsyncConsole.WriteLine("""
            titanium reload -c <config> [--pid <pid>]

              -c, --config   Config path used by the running `titanium run` (required on Windows).
              --pid          Process id (Unix only; optional when -c finds the pid file).

            Reloads routes, clusters, and server: knobs without dropping listeners or in-flight
            requests. On Unix this sends SIGHUP; on Windows it signals a named event.
            Listeners, certificates, Plus plugins, and static files still require a process restart.
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }

    private static string? TryParseConfigPath(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if ((args[i] is "-c" or "--config") && i + 1 < args.Length)
                return args[i + 1];
        }

        return null;
    }

    private static int? TryParsePid(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--pid" && i + 1 < args.Length &&
                int.TryParse(args[i + 1], out var pid) && pid > 0)
            {
                return pid;
            }
        }

        return null;
    }
}
