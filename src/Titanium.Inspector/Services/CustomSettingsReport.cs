namespace Titanium.Inspector.Services;

/// <summary>
/// Network and protocol settings that differ from factory defaults.
/// Other preferences (theme, language, columns) are not listed.
/// </summary>
public static class CustomSettingsReport
{
    public const string Upstream = "custom.upstream";
    public const string Http2Off = "custom.http2Off";
    public const string Http3Off = "custom.http3Off";
    public const string ReuseOff = "custom.reuseOff";

    public readonly record struct Deviation(string TextKey, string? Suffix);

    public static IReadOnlyList<Deviation> ListDeviations(InspectorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var items = new List<Deviation>(5);
        if (settings.UpstreamProxyEnabled)
        {
            items.Add(new Deviation(Upstream, InspectorNetworkSettings.FormatUpstreamUri(settings)));
        }

        if (!settings.EnableHttp2)
            items.Add(new Deviation(Http2Off, null));

        if (!settings.EnableHttp3)
            items.Add(new Deviation(Http3Off, null));

        var preset = settings.OutboundTlsPreset;
        if (string.Equals(preset, InspectorNetworkSettings.Tls12, StringComparison.OrdinalIgnoreCase))
            items.Add(new Deviation("network.tls12", null));
        else if (string.Equals(preset, InspectorNetworkSettings.TlsLegacy, StringComparison.OrdinalIgnoreCase))
            items.Add(new Deviation("network.tlsLegacy", null));
        else if (!string.Equals(preset, InspectorNetworkSettings.TlsDefault, StringComparison.OrdinalIgnoreCase))
            items.Add(new Deviation("network.tls", null));

        if (!settings.ReuseServerConnections)
            items.Add(new Deviation(ReuseOff, null));

        if (!string.IsNullOrWhiteSpace(settings.WinAuthHosts))
            items.Add(new Deviation("network.winAuth", null));

        return items;
    }

    /// <summary>Restores only the settings <see cref="ListDeviations"/> reports. Other preferences stay.</summary>
    public static void RestoreDefaults(InspectorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.UpstreamProxyEnabled = false;
        settings.UpstreamProxyHost = string.Empty;
        settings.UpstreamProxyPort = InspectorNetworkSettings.DefaultUpstreamPort;
        settings.EnableHttp2 = true;
        settings.EnableHttp3 = true;
        settings.OutboundTlsPreset = InspectorNetworkSettings.TlsDefault;
        settings.ReuseServerConnections = true;
        settings.WinAuthHosts = string.Empty;
    }
}
