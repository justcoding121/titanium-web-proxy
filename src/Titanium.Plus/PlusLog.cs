using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus;

/// <summary>Thin helper so Plus modules log through the host <see cref="PlusActivationContext.Logger"/>.</summary>
internal static class PlusLog
{
    public static void Info(PlusActivationContext context, string message)
    {
        if (context.Logger is { } logger && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("{Message}", message);
        // No sync Console fallback: hosts must supply ILogger when diagnostics are wanted.
    }

    public static void Warn(PlusActivationContext context, string message)
    {
        if (context.Logger is { } logger && logger.IsEnabled(LogLevel.Warning))
            logger.LogWarning("{Message}", message);
    }

    public static void Error(PlusActivationContext context, string message)
    {
        if (context.Logger is { } logger && logger.IsEnabled(LogLevel.Error))
            logger.LogError("{Message}", message);
    }
}
