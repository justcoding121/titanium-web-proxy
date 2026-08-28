using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Security;

/// <summary>Stretch: JWT/OIDC + CIDR allow/deny middleware (opt-in).</summary>
public sealed class AccessSecurity
{
    public static AccessSecurity? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        var hasJwt = options.ContainsKey("security.jwtAuthority");
        var hasCidr = options.ContainsKey("security.allowCidrs");
        if (!hasJwt && !hasCidr)
        {
            return null;
        }

        Console.WriteLine("Plus Security: JWT/CIDR options present — register IProxyMiddleware when wiring completes.");
        _ = context;
        return new AccessSecurity();
    }
}

/// <summary>Legacy stub type name.</summary>
public sealed class SecurityPlaceholder;
