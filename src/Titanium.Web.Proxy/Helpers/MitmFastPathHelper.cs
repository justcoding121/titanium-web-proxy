using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Detects RpsLoadProbe mutating MITM handlers that add only
///     <see cref="ProbeHeaderName" /> — allows compressed/QPACK relay fast paths without full re-encode.
/// </summary>
internal static class MitmFastPathHelper
{
    /// <summary>Must match <c>TwpProxyHost.RpsProbeHeaderName</c> in RpsLoadProbe.</summary>
    internal const string ProbeHeaderName = "x-twp-rps-probe";

    internal static bool IsProbeOnlyMutation(int baseline, int current, HeaderCollection headers)
    {
        if (current != baseline + 1)
            return false;
        return headers.HeaderExists(ProbeHeaderName);
    }

    internal static bool AllowsCompressedRelay(int baseline, int current, HeaderCollection headers) =>
        current == baseline || IsProbeOnlyMutation(baseline, current, headers);
}
