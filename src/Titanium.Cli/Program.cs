using Titanium.Cli.Config;
using Titanium.Cli.Updates;

namespace Titanium.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        try
        {
            return command switch
            {
                "run" => await RunCommand.ExecuteAsync(ParseConfigPath(args), ParseVerbose(args)),
                "test" => TestCommand.Execute(ParseConfigPath(args)),
                "version" => await VersionCommand.ExecuteAsync(args),
                "update" => await UpdateCommand.ExecuteAsync(args),
                "help" or "-h" or "--help" => PrintHelp(),
                _ => await UnknownAsync(command),
            };
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static string ParseConfigPath(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if ((args[i] is "-c" or "--config") && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        throw new ArgumentException("Missing required -c <config-path>.");
    }

    private static bool ParseVerbose(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "-v" or "--verbose")
            {
                return true;
            }
        }

        return false;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            Titanium Web Proxy CLI

            Usage:
              titanium run -c <config> [-v|--verbose]
              titanium test -c <config>
              titanium version [--check] [--plus] [--channel beta]
              titanium update [--plus] [--channel beta]
            """);
        return 0;
    }

    private static async Task<int> UnknownAsync(string command)
    {
        await Console.Error.WriteLineAsync($"Unknown command: {command}");
        PrintHelp();
        return 1;
    }
}
