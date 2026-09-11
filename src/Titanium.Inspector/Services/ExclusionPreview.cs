namespace Titanium.Inspector.Services;

/// <summary>Formats exclusion summaries and opaque-tunnel reasons for Inspector UI.</summary>
public static class ExclusionPreview
{
    public static string ExclusionSummary(InspectorSettings settings, int learnedCount = 0)
    {
        var bypass = settings.SystemProxyBypassHosts?.Count(h => !string.IsNullOrWhiteSpace(h)) ?? 0;
        var tunnel = settings.DecryptSkipHosts?.Count(h => !string.IsNullOrWhiteSpace(h)) ?? 0;
        if (bypass == 0 && tunnel == 0 && learnedCount == 0)
        {
            return "";
        }

        var parts = new List<string>();
        if (bypass > 0 || tunnel > 0)
            parts.Add($"Exclusions: {bypass} OS bypass, {tunnel} tunnel-only");
        if (learnedCount > 0)
            parts.Add($"Learned: {learnedCount}");
        return string.Join(" · ", parts);
    }

    public static string DescribeOpaqueReason(OpaqueTunnelReason reason) => reason switch
    {
        OpaqueTunnelReason.DecryptOff => "Encrypted: Decrypt HTTPS is off",
        OpaqueTunnelReason.BuiltInIdentity => "Encrypted: Microsoft identity bypass (tunnel)",
        OpaqueTunnelReason.BuiltInPinning => "Encrypted: pinning host (tunnel only)",
        OpaqueTunnelReason.UserSkipList => "Encrypted: tunnel-only exclusion list",
        OpaqueTunnelReason.UserOnlyList => "Encrypted: not on decrypt-only allowlist",
        OpaqueTunnelReason.LearnedFailure => "Encrypted: auto-tunneled after decrypt failure",
        _ => "",
    };
}
