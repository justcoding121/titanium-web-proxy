using System;

namespace Titanium.Web.Proxy.Masque;

/// <summary>Request checks shared by the MASQUE listener and its tests.</summary>
internal static class MasqueRequestChecks
{
    /// <summary>
    ///     RFC 9298 makes <c>capsule-protocol</c> optional. When present it is an RFC 8941 boolean:
    ///     <c>?1</c> or <c>?1</c> followed by parameters. <c>?0</c> and any other value are rejected.
    /// </summary>
    public static bool CapsuleProtocolAccepted(string? value)
    {
        if (value is null)
        {
            return true;
        }

        var trimmed = value.Trim();
        return trimmed.Equals("?1", StringComparison.Ordinal)
               || trimmed.StartsWith("?1;", StringComparison.Ordinal);
    }
}
