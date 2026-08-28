using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus;

/// <summary>Thin helper so Plus modules log through the host <see cref="PlusActivationContext.Logger"/>.</summary>
internal static class PlusLog
{
    public static void Info(PlusActivationContext context, string message)
    {
        if (context.Logger is { } logger)
        {
            logger.LogInformation("{Message}", message);
            return;
        }

        Console.WriteLine(message);
    }

    public static void Warn(PlusActivationContext context, string message)
    {
        if (context.Logger is { } logger)
        {
            logger.LogWarning("{Message}", message);
            return;
        }

        Console.Error.WriteLine(message);
    }

    public static void Error(PlusActivationContext context, string message)
    {
        if (context.Logger is { } logger)
        {
            logger.LogError("{Message}", message);
            return;
        }

        Console.Error.WriteLine(message);
    }
}
