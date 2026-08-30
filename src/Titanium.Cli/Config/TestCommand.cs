using Titanium.Cli;
using Titanium.Cli.Parsers;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Cli.Config;

internal static class TestCommand
{
    public static Task<int> ExecuteAsync(string configPath)
    {
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

        var needsSession = RunCommand.ConfigNeedsSessionPath(loaded.Config);
        AsyncConsole.WriteLine($"Config OK: {configPath}");
        AsyncConsole.WriteLine($"Routes: {loaded.Config.Routes.Count}, Clusters: {loaded.Config.Clusters.Count}, Listeners: {loaded.Config.Listeners.Count}");
        AsyncConsole.WriteLine($"EnableHttpInterception would be: {needsSession} (auto when transforms/static/ACME)");
        AsyncConsole.WriteLine($"EnableRequestTimingCapture would be: {RunCommand.ConfigNeedsRequestTimingCapture(loaded.Config)} (auto when LeastTime LB)");
        return Task.FromResult(0);
    }
}
