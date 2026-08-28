using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus;

/// <summary>Thin helper so Plus modules log through the host <see cref="PlusActivationContext.Logger"/>.</summary>
internal static class PlusLog
{
    public static void Info(PlusActivationContext context, string message) =>
        Log(context, LogLevel.Information, message);

    public static void Warn(PlusActivationContext context, string message) =>
        Log(context, LogLevel.Warning, message);

    public static void Error(PlusActivationContext context, string message) =>
        Log(context, LogLevel.Error, message);

    private static void Log(PlusActivationContext context, LogLevel level, string message)
    {
        var logger = context.Logger;
        if (logger is not null)
        {
            logger.Log(level, "{Message}", message);
            return;
        }

        // Fallback when host did not supply a logger (should not happen for CLI).
        if (level >= LogLevel.Warning)
        {
            Console.Error.WriteLine(message);
        }
        else
        {
            Console.WriteLine(message);
        }
    }
}
