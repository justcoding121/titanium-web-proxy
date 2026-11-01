using System.Diagnostics;
using Titanium.Cli.Parsers;

namespace Titanium.Cli.Config;

/// <summary>How a <see cref="RestartCommand.WaitForTakeover" /> ended.</summary>
internal enum RestartOutcome
{
    /// <summary>The pid file names the new process: it is ready and has signaled the old one.</summary>
    TookOver,

    /// <summary>The new process exited before taking over (crash or config error). The old one keeps serving.</summary>
    ChildExited,

    /// <summary>The new process is still alive but did not take over in time.</summary>
    TimedOut,
}

/// <summary>
///     <c>titanium restart -c &lt;config&gt;</c>: Linux hot restart helper. Starts a new <c>titanium run</c> with the same
///     config (it binds with SO_REUSEPORT beside the running one), waits until that process is ready and has taken
///     over the pid file, and reports. The old process drains and exits on its own; this command never kills it.
/// </summary>
internal static class RestartCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (CliHelp.RequestsHelp(args.AsSpan(1)))
        {
            return PrintHelp();
        }

        try
        {
            var configPath = ParseConfigPath(args)
                             ?? throw new ArgumentException("Provide -c <config> (the config of the running process).");
            if (!OperatingSystem.IsLinux())
            {
                throw new InvalidOperationException("titanium restart (hot restart) is supported on Linux only.");
            }

            var hotRestart = ConfigLoader.Load(configPath).Config.Server?.HotRestart;
            if (hotRestart is not { Enabled: true } || string.IsNullOrWhiteSpace(hotRestart.PidFile))
            {
                throw new InvalidOperationException(
                    "server.hotRestart.enabled with server.hotRestart.pidFile is required for titanium restart.");
            }

            var pidFile = Path.GetFullPath(hotRestart.PidFile);
            var timeout = TimeSpan.FromSeconds(hotRestart.HandoverTimeoutSeconds ?? RunCommand.DefaultHandoverTimeoutSeconds);
            var previous = ReadPid(pidFile);

            var logFile = pidFile + ".log";
            var startInfo = BuildStartInfo(args, logFile);
            using var child = Process.Start(startInfo)
                              ?? throw new InvalidOperationException("Could not start the new titanium process.");
            AsyncConsole.WriteLine(
                $"Started new process {child.Id}" +
                (previous is { } p ? $"; current process {p} keeps serving until it takes over." : ".") +
                $" Its output goes to {logFile}.");

            var outcome = await WaitForTakeover(
                () => ReadPid(pidFile),
                child.Id,
                () => !child.HasExited,
                timeout,
                TimeSpan.FromMilliseconds(200),
                CancellationToken.None).ConfigureAwait(false);

            switch (outcome)
            {
                case RestartOutcome.TookOver:
                    AsyncConsole.WriteLine(
                        $"Process {child.Id} is ready and owns {pidFile}." +
                        (previous is { } old ? $" Process {old} is draining and will exit on its own." : string.Empty));
                    return 0;
                case RestartOutcome.ChildExited:
                    AsyncConsole.WriteError(
                        $"New process {child.Id} exited with code {child.ExitCode} before it was ready. " +
                        "The previous process was not signaled and keeps serving." + TailOf(logFile));
                    return 1;
                default:
                    AsyncConsole.WriteError(
                        $"New process {child.Id} did not take over within {timeout.TotalSeconds:0}s. " +
                        "It is still running; check " + logFile + ". The previous process keeps serving." + TailOf(logFile));
                    return 1;
            }
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError(ex.Message);
            return 1;
        }
    }

    /// <summary>Polls until the pid file names <paramref name="childPid" />, the child dies, or the timeout passes.</summary>
    internal static async Task<RestartOutcome> WaitForTakeover(
        Func<int?> readPidFile,
        int childPid,
        Func<bool> childAlive,
        TimeSpan timeout,
        TimeSpan poll,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            // The pid file wins over a racing exit check: a child that took over and was then stopped still took over.
            if (readPidFile() == childPid)
            {
                return RestartOutcome.TookOver;
            }

            if (!childAlive())
            {
                return readPidFile() == childPid ? RestartOutcome.TookOver : RestartOutcome.ChildExited;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return RestartOutcome.TimedOut;
            }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The arguments of the new process: <c>run</c> plus everything after <c>restart</c>.</summary>
    internal static IReadOnlyList<string> BuildRunArguments(string[] args) => ["run", .. args.Skip(1)];

    /// <summary>
    ///     <c>sh -c 'exec titanium run … &lt;/dev/null &gt;&gt;log 2&gt;&amp;1'</c>: the new process keeps no handle on this
    ///     command's stdio (a caller that captures our output would otherwise wait for it to exit) and has the same
    ///     pid as the shell because of <c>exec</c>.
    /// </summary>
    private static ProcessStartInfo BuildStartInfo(string[] args, string logFile)
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("The path of the running executable is unknown.");
        var startInfo = new ProcessStartInfo { FileName = "/bin/sh", UseShellExecute = false };
        startInfo.Environment["TWP_RESTART_LOG"] = logFile;
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec \"$0\" \"$@\" </dev/null >>\"$TWP_RESTART_LOG\" 2>&1");
        startInfo.ArgumentList.Add(processPath);

        // Started through the dotnet host (framework-dependent): the entry assembly is the first argument.
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            && Environment.GetCommandLineArgs() is { Length: > 0 } commandLine)
        {
            startInfo.ArgumentList.Add(commandLine[0]);
        }

        foreach (var arg in BuildRunArguments(args))
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private static string TailOf(string logFile)
    {
        try
        {
            var lines = File.ReadAllLines(logFile);
            return lines.Length == 0
                ? string.Empty
                : Environment.NewLine + string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - 10)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static int? ReadPid(string pidFile)
    {
        try
        {
            return int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && pid > 0 ? pid : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ParseConfigPath(string[] args)
    {
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] is "-c" or "--config")
            {
                return args[i + 1];
            }
        }

        return null;
    }

    internal static int PrintHelp()
    {
        AsyncConsole.WriteLine("""
            titanium restart -c <config> [run options]

              -c, --config   Config of the running `titanium run` (needs server.hotRestart enabled, Linux only).

            Starts a new `titanium run` (output appended to <pidFile>.log) beside the running one (SO_REUSEPORT), waits
            until it is ready, and lets it take over: the old process stops accepting, drains in-flight work
            for server.hotRestart.handoverTimeoutSeconds, and exits. HTTP/3 and UDP listeners bind once the
            old process has exited. If the new process fails to start, the old one keeps serving.
            Under a supervisor (systemd), restart through the supervisor instead.
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }
}
