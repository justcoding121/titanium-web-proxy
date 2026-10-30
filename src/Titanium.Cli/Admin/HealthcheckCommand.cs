using System.Net;
using System.Net.Sockets;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Admin;

/// <summary>
///     <c>titanium healthcheck</c> — one liveness probe against the admin listener, for container
///     <c>HEALTHCHECK</c> (the runtime image has no shell or curl). Exit 0 is healthy.
/// </summary>
internal static class HealthcheckCommand
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (CliHelp.RequestsHelp(args.AsSpan(1)))
        {
            return PrintHelp();
        }

        try
        {
            string? url = null;
            string? configPath = null;
            var timeout = DefaultTimeout;
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--url" when i + 1 < args.Length:
                        url = args[i + 1];
                        i++;
                        break;
                    case "-c" or "--config" when i + 1 < args.Length:
                        configPath = args[i + 1];
                        i++;
                        break;
                    case "--timeout" when i + 1 < args.Length:
                        if (!double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
                            seconds <= 0 || seconds > 300)
                        {
                            throw new ArgumentException("--timeout must be between 0 and 300 seconds.");
                        }

                        timeout = TimeSpan.FromSeconds(seconds);
                        i++;
                        break;
                }
            }

            if (url is null)
            {
                if (configPath is null)
                {
                    throw new ArgumentException("Provide --url <url> or -c <config>.");
                }

                var resolved = ResolveUrl(TwpConfigLoader.LoadFile(configPath), out var skipReason);
                if (resolved is null)
                {
                    AsyncConsole.WriteLine("skipped: " + skipReason);
                    return 0;
                }

                url = resolved;
            }

            return await ProbeAsync(url, timeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError(ex.Message);
            return 1;
        }
    }

    /// <summary>
    ///     The <c>/healthz</c> URL for the admin listener in <paramref name="config" />, or null (with a reason) when
    ///     there is nothing a probe could reach: the listener is off, or it binds an OS-chosen port.
    /// </summary>
    internal static string? ResolveUrl(TwpConfig config, out string? skipReason)
    {
        var admin = config.Server?.Admin;
        if (admin is not { Enabled: true })
        {
            skipReason = "server.admin is not enabled";
            return null;
        }

        if (admin.Port is null or 0)
        {
            skipReason = "server.admin.port is 0 (OS-chosen), so the probe cannot know where to connect";
            return null;
        }

        var host = string.IsNullOrWhiteSpace(admin.Host) ? "127.0.0.1" : admin.Host.Trim();
        if (!IPAddress.TryParse(host, out var address))
        {
            throw new ArgumentException($"server.admin.host '{host}' is not an IP address.");
        }

        // A wildcard bind is reachable on loopback.
        if (address.Equals(IPAddress.Any))
        {
            address = IPAddress.Loopback;
        }
        else if (address.Equals(IPAddress.IPv6Any))
        {
            address = IPAddress.IPv6Loopback;
        }

        var authority = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        skipReason = null;
        // Local admin healthcheck uses cleartext HTTP to the process's own admin port.
        return $"http://{authority}:{admin.Port}/healthz"; // NOSONAR S5332 -- local admin probe is intentionally cleartext.
    }

    private static async Task<int> ProbeAsync(string url, TimeSpan timeout)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = timeout };
        using var http = new HttpClient(handler) { Timeout = timeout };
        try
        {
            using var response = await http.GetAsync(url).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                AsyncConsole.WriteLine("ok");
                return 0;
            }

            AsyncConsole.WriteError($"unhealthy: {(int)response.StatusCode} from {url}");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AsyncConsole.WriteError($"unhealthy: cannot reach {url} ({ex.Message})");
            return 1;
        }
    }

    internal static int PrintHelp()
    {
        AsyncConsole.WriteLine("""
            titanium healthcheck (-c <config> | --url <url>) [--timeout <seconds>]

              -c, --config   Read server.admin from this config and probe /healthz on its host and port.
                             Exits 0 without probing when the admin listener is off or uses port 0.
              --url          Probe this URL instead (for example http://127.0.0.1:9901/healthz).
              --timeout      Seconds to wait for the answer. Default 3.

            Exit 0 when the endpoint answers 2xx, 1 otherwise. Used by the container HEALTHCHECK.
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }
}
