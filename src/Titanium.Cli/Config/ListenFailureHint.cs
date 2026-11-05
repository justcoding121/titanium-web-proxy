using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Exceptions;

namespace Titanium.Cli.Config;

/// <summary>Stderr text for a listener that could not bind.</summary>
internal static class ListenFailureHint
{
    /// <summary>
    ///     Returns the bind error plus the config key, and a same-config pid hint when one is live.
    /// </summary>
    internal static string Format(
        ProxyListenException ex,
        TwpConfig config,
        string configPath,
        Func<int, bool>? isAlive = null,
        int? currentPid = null,
        Func<string, int?>? readPid = null)
    {
        var text = ex.Message;
        var listeners = config.Listeners;
        if (listeners is not null)
        {
            for (var i = 0; i < listeners.Count; i++)
            {
                if (listeners[i].Port == ex.Port)
                {
                    text += $" (config: listeners[{i}].port)";
                    break;
                }
            }
        }

        if (!ex.IsAddressInUse)
        {
            return text;
        }

        var pid = (readPid ?? ConfigReloadGate.TryReadPid)(configPath);
        var alive = isAlive ?? ConfigReloadGate.IsProcessAlive;
        var self = currentPid ?? Environment.ProcessId;
        if (pid is int found && found != self && alive(found))
        {
            text += Environment.NewLine +
                    $"A titanium process (PID {found}) started with this config may be holding the port. " +
                    $"Use 'titanium reload -c {configPath}' or 'titanium restart -c {configPath}' instead of starting a second copy.";
        }

        return text;
    }
}
