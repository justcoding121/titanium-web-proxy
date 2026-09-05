namespace Titanium.Cli.QaProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var cmd = args.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "help";
        var elevated = args.Any(a => a is "--elevated");

        if (cmd is "help" or "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        using var log = new ProbeLog();
        ScenarioRunner.TryDisableSystemProxy();
        log.Info($"CliQaProbe command={cmd} elevatedFlag={elevated} isAdmin={Elevation.IsElevated()}");

        try
        {
            var code = cmd switch
            {
                "status" => await ScenarioRunner.RunStatusAsync(log),
                "help-matrix" => await ScenarioRunner.RunHelpMatrixAsync(log),
                "service" => await ScenarioRunner.RunServiceSectionAsync(log, elevated),
                "core" => await RunCoreAsync(log),
                "all" => await RunAllAsync(log, elevated),
                _ => Unknown(log, cmd),
            };

            log.WriteSummary(cmd, code);
            return code;
        }
        catch (Exception ex)
        {
            log.Step("fatal", false, ex.ToString());
            log.WriteSummary(cmd, 1);
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(ProbeLog log)
    {
        var fails = 0;
        fails += await ScenarioRunner.RunHelpMatrixAsync(log);
        fails += await ScenarioRunner.RunTestDialectsAsync(log);
        fails += await ScenarioRunner.RunLiveCoreAsync(log);
        fails += await ScenarioRunner.RunCoreTrafficExtrasAsync(log);
        return fails == 0 ? 0 : 1;
    }

    private static async Task<int> RunAllAsync(ProbeLog log, bool elevated)
    {
        var fails = 0;
        fails += await ScenarioRunner.RunHelpMatrixAsync(log);
        fails += await ScenarioRunner.RunMetaAsync(log);
        fails += await ScenarioRunner.RunTestDialectsAsync(log);
        fails += await ScenarioRunner.RunLiveCoreAsync(log);
        fails += await ScenarioRunner.RunLiveRemainingAsync(log);
        fails += await ScenarioRunner.RunServiceSectionAsync(log, elevated);
        return fails == 0 ? 0 : 1;
    }

    private static int Unknown(ProbeLog log, string cmd)
    {
        log.Step("unknown", false, $"Unknown command '{cmd}'");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            CliQaProbe — per-machine Titanium CLI checklist (not CI)

              dotnet run --project tools/CliQaProbe -- <command> [--elevated]

            Commands:
              status         titanium.dll path, OS, elevation, last-run.json
              help-matrix    Nested --help including service subcommands
              service        Service status-missing + unelevated install message;
                             with --elevated: install/start/HTTP/stop/uninstall
                             (name titanium-qa-probe only)
              core           Help + dialects + forward/nginx/static/mitm/logging
              all            core + sitefile/routes/tls/http2/plus/meta + unelevated
                             service checks; --elevated adds live SCM lifecycle

            Examples:
              dotnet run --project tools/CliQaProbe -- all
              dotnet run --project tools/CliQaProbe -- all --elevated

            Results: tools/CliQaProbe/results/last-run.json
            See also: tools/LOCAL-QA.md
            """);
    }
}
