namespace Titanium.Cli;

/// <summary>Shared argv helpers for nested <c>help</c> / <c>-h</c> / <c>--help</c>.</summary>
internal static class CliHelp
{
    public const string DocsUrl = "https://titaniumproxy.com/docs/cli";

    public static bool IsHelpToken(string? arg) =>
        arg is "help" or "-h" or "--help";

    /// <summary>True when any remaining argv token is help (before required-flag parsing).</summary>
    public static bool RequestsHelp(ReadOnlySpan<string> args)
    {
        foreach (var a in args)
        {
            if (IsHelpToken(a))
            {
                return true;
            }
        }

        return false;
    }

    public static void WriteDocsFooter() =>
        AsyncConsole.WriteLine($"Docs: {DocsUrl}");
}
