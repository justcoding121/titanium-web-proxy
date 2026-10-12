using System.Security.Authentication;

namespace Titanium.Inspector.Services;

/// <summary>
/// Upstream proxy address for Inspector. Applied when capture starts; a running proxy keeps
/// the address it was started with.
/// </summary>
public static class InspectorNetworkSettings
{
    public const int DefaultUpstreamPort = 8080;
    public const string TlsDefault = "Default";
    public const string Tls12 = "Tls12";
    public const string TlsLegacy = "Legacy";

    public const string HostRequired = "network.hostRequired";
    public const string PortInvalid = "network.portInvalid";
    public const string SelfLoop = "network.self";
    public const string HostInvalid = "network.hostInvalid";

    public static void ApplyTo(InterceptionService interception, InspectorSettings settings)
    {
        interception.UpstreamProxyAddress = FormatUpstreamUri(settings);
        interception.PreferHttp2 = settings.EnableHttp2;
        interception.PreferHttp3 = settings.EnableHttp3;
        interception.OutboundTlsProtocols = MapOutboundTls(settings.OutboundTlsPreset);
        interception.ReuseServerConnections = settings.ReuseServerConnections;
        interception.WinAuthHosts = settings.WinAuthHosts;
        interception.ApplyHttpProtocols();
    }

    public static string ProtocolSummaryKey(bool http2, bool http3) => (http2, http3) switch
    {
        (false, false) => "network.summaryH1",
        (true, false) => "network.summaryH2",
        (false, true) => "network.summaryH3",
        _ => "network.summaryBoth",
    };

    /// <summary>
    /// Outbound mask only. <see cref="SslProtocols.None"/> keeps the engine default (TLS 1.2 and 1.3).
    /// Never applied to browser-facing TLS.
    /// </summary>
    public static SslProtocols MapOutboundTls(string? preset)
    {
        if (string.Equals(preset, Tls12, StringComparison.OrdinalIgnoreCase))
        {
            return SslProtocols.Tls12;
        }

        if (string.Equals(preset, TlsLegacy, StringComparison.OrdinalIgnoreCase))
        {
#pragma warning disable SYSLIB0039
            return SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Tls13; // NOSONAR S4423 -- intentional Legacy preset for migration; UI warns and OS may still refuse
#pragma warning restore SYSLIB0039
        }

        return SslProtocols.None;
    }

    public static string? FormatUpstreamUri(InspectorSettings settings)
    {
        if (!settings.UpstreamProxyEnabled)
        {
            return null;
        }

        return FormatUpstreamUri(settings.UpstreamProxyHost, settings.UpstreamProxyPort);
    }

    public static string? FormatUpstreamUri(string? host, int port)
    {
        var normalized = NormalizeHost(host);
        if (normalized.Length == 0 || port is < 1 or > 65535)
        {
            return null;
        }

        var authority = normalized.Contains(':') ? "[" + normalized + "]" : normalized;
        return "http://" + authority + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Null when the draft is valid. Otherwise a localization key.</summary>
    public static string? Validate(bool enabled, string? host, int port, string bindAddress, int bindPort)
    {
        if (!enabled)
        {
            return null;
        }

        var normalized = NormalizeHost(host);
        if (normalized.Length == 0)
        {
            return HostRequired;
        }

        if (port is < 1 or > 65535)
        {
            return PortInvalid;
        }

        if (Uri.CheckHostName(normalized) == UriHostNameType.Unknown)
        {
            return HostInvalid;
        }

        if (port == bindPort && string.Equals(normalized, NormalizeHost(bindAddress), StringComparison.OrdinalIgnoreCase))
        {
            return SelfLoop;
        }

        return null;
    }

    public static void ResetToDefaults(InspectorSettings settings)
    {
        settings.UpstreamProxyEnabled = false;
        settings.UpstreamProxyHost = string.Empty;
        settings.UpstreamProxyPort = DefaultUpstreamPort;
        settings.EnableHttp2 = true;
        settings.EnableHttp3 = true;
        settings.OutboundTlsPreset = TlsDefault;
        settings.ReuseServerConnections = true;
        settings.WinAuthHosts = string.Empty;
    }

    private static string NormalizeHost(string? host)
    {
        var value = host?.Trim() ?? string.Empty;
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            value = value[1..^1];
        }

        return value;
    }
}
