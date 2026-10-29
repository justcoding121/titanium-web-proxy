using System.Security.Cryptography;
using System.Text;

namespace Titanium.Cli.Config;

/// <summary>Plus utility commands that do not need a running proxy (<c>titanium plus …</c>).</summary>
internal static class PlusCommand
{
    private const int DefaultIterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length < 2 ||
            args[1] is "-h" or "--help" ||
            (args.Length >= 2 && args[1].Equals("help", StringComparison.OrdinalIgnoreCase)))
        {
            PrintHelp();
            return Task.FromResult(args.Length < 2 ? 1 : 0);
        }

        var sub = args[1].ToLowerInvariant();
        return sub switch
        {
            "hash-password" => Task.FromResult(HashPassword(args.AsSpan(2).ToArray())),
            _ => Unknown(sub),
        };
    }

    private static Task<int> Unknown(string sub)
    {
        AsyncConsole.WriteError($"Unknown plus command: {sub}");
        PrintHelp();
        return Task.FromResult(1);
    }

    private static int HashPassword(string[] args)
    {
        string? user = null;
        var role = "operator";
        string? password = null;
        var iterations = DefaultIterations;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                AsyncConsole.WriteLine("""
                    Usage: titanium plus hash-password [--user <name>] [--role operator|viewer] [--iterations N] [password]

                    Prints one local-users file line for plus.controlPlane.dashboardUsersFile:
                      user:role:iterations:saltBase64:hashBase64

                    When password is omitted, it is read from standard input (one line).
                    """);
                return 0;
            }

            if (arg is "--user" && i + 1 < args.Length)
            {
                user = args[++i];
                continue;
            }

            if (arg is "--role" && i + 1 < args.Length)
            {
                var text = args[++i];
                if (text.Equals("operator", StringComparison.OrdinalIgnoreCase))
                {
                    role = "operator";
                }
                else if (text.Equals("viewer", StringComparison.OrdinalIgnoreCase))
                {
                    role = "viewer";
                }
                else
                {
                    AsyncConsole.WriteError("--role must be operator or viewer.");
                    return 1;
                }

                continue;
            }

            if (arg is "--iterations" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out iterations) || iterations < 10_000)
                {
                    AsyncConsole.WriteError("--iterations must be an integer >= 10000.");
                    return 1;
                }

                continue;
            }

            if (arg.StartsWith('-'))
            {
                AsyncConsole.WriteError($"Unknown option: {arg}");
                return 1;
            }

            password = arg;
        }

        user ??= "user";
        if (user.Contains(':', StringComparison.Ordinal) || user.Contains('#', StringComparison.Ordinal))
        {
            AsyncConsole.WriteError("username must not contain ':' or '#'.");
            return 1;
        }

        password ??= ReadPassword();
        if (string.IsNullOrEmpty(password))
        {
            AsyncConsole.WriteError("Password must not be empty.");
            return 1;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        AsyncConsole.WriteLine(
            $"{user}:{role}:{iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}");
        return 0;
    }

    private static string? ReadPassword()
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        AsyncConsole.Write("Password: ");
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
            }
        }

        return new string(chars.ToArray());
    }

    private static void PrintHelp()
    {
        AsyncConsole.WriteLine("""
            Titanium Plus utilities

            Usage:
              titanium plus hash-password [--user <name>] [--role operator|viewer] [--iterations N] [password]
            """);
        CliHelp.WriteDocsFooter();
    }
}
