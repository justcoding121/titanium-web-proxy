using Titanium.Cli;
using Titanium.Cli.Parsers;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Cli.Config;

internal static class TestCommand
{
    public static Task<int> ExecuteAsync(string[] args)
    {
        if (CliHelp.RequestsHelp(args.AsSpan(1)))
        {
            return Task.FromResult(PrintHelp());
        }

        var strict = args.Any(a => string.Equals(a, "--strict", StringComparison.OrdinalIgnoreCase));
        var configPath = RunCommand.ParseConfigPath(args);
        var loaded = ConfigLoader.Load(configPath);
        var errors = TwpConfigValidator.Validate(loaded.Config);
        if (errors.Count > 0)
        {
            foreach (var e in errors)
            {
                AsyncConsole.WriteError(e);
            }

            return Task.FromResult(1);
        }

        foreach (var warning in loaded.UnknownKeys)
            AsyncConsole.WriteError("warning: " + warning);
        foreach (var warning in TwpConfigValidator.CollectWarnings(loaded.Config))
            AsyncConsole.WriteError("warning: " + warning);
        if (strict && loaded.UnknownKeys.Count > 0)
            return Task.FromResult(1);

        var needsSession = RunCommand.ConfigNeedsSessionPath(loaded.Config);
        AsyncConsole.WriteLine($"Config OK: {configPath}");
        AsyncConsole.WriteLine($"Routes: {loaded.Config.Routes.Count}, Clusters: {loaded.Config.Clusters.Count}, Listeners: {loaded.Config.Listeners.Count}");
        AsyncConsole.WriteLine($"EnableHttpInterception would be: {needsSession} (auto when transforms/static/ACME)");
        AsyncConsole.WriteLine($"EnableRequestTimingCapture would be: {RunCommand.ConfigNeedsRequestTimingCapture(loaded.Config)} (auto when LeastTime LB)");
        return Task.FromResult(0);
    }

    internal static int PrintHelp()
    {
        AsyncConsole.WriteLine("""
            titanium test -c <config> [--strict]

              -c, --config   Path to twp.yaml / .json / .twp / .conf (required).
              --strict       Exit 1 when the file contains unknown keys.

            Validates the config without opening listeners or serving traffic.
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }
}
