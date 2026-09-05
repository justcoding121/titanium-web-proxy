using Titanium.Cli.Config;
using Titanium.Cli.Http3;
using Titanium.Cli.Updates;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Framework-dependent macOS Debug: make app-local libmsquic visible to QuicListener.
        Http3NativeBootstrap.EnsureAppLocalMsQuicVisible(args);

        try
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
                    "test" => await TestCommand.ExecuteAsync(ParseConfigPath(args)),
                    "version" => await VersionCommand.ExecuteAsync(args),
                    "update" => await UpdateCommand.ExecuteAsync(args),
                    "http3-deps" => await Http3DepsCommand.ExecuteAsync(args),
                    "help" or "-h" or "--help" => PrintHelp(),
                    _ => await UnknownAsync(command),
                };
            }
            catch (Exception ex)
            {
                AsyncConsole.WriteError(ex.Message);
                return 1;
            }
        }
        finally
        {
            await AsyncConsole.FlushAsync();
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
        AsyncConsole.WriteLine("""
            Titanium Web Proxy CLI

            Usage:
              titanium run -c <config> [-v|--verbose]
              titanium test -c <config>
              titanium version [--check] [--plus] [--channel beta]
              titanium update [--plus] [--channel beta]
              titanium http3-deps status|install
            """);
        return 0;
    }

    private static Task<int> UnknownAsync(string command)
    {
        AsyncConsole.WriteError($"Unknown command: {command}");
        PrintHelp();
        return Task.FromResult(1);
    }
}
