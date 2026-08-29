using Titanium.Cli.Parsers;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Cli.Config;

internal static class TestCommand
{
    public static int Execute(string configPath)
    {
        var loaded = ConfigLoader.Load(configPath);
        var errors = TwpConfigValidator.Validate(loaded.Config);
        if (errors.Count > 0)
        {
            foreach (var e in errors)
            {
                Console.Error.WriteLine(e);
            }

            return 1;
        }

        var needsSession = RunCommand.ConfigNeedsSessionPath(loaded.Config);
        Console.WriteLine($"Config OK: {configPath}");
        Console.WriteLine($"Routes: {loaded.Config.Routes.Count}, Clusters: {loaded.Config.Clusters.Count}, Listeners: {loaded.Config.Listeners.Count}");
        Console.WriteLine($"EnableHttpInterception would be: {needsSession} (auto when transforms/static/ACME)");
        Console.WriteLine($"EnableRequestTimingCapture would be: {RunCommand.ConfigNeedsRequestTimingCapture(loaded.Config)} (auto when LeastTime LB)");
        return 0;
    }
}
