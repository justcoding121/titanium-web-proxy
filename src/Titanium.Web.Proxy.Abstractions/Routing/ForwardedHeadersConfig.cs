namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>What to do with one forwarded header. Off leaves it untouched.</summary>
public enum ForwardedHeaderAction
{
    Off = 0,
    Set = 1,
    Append = 2,
    Remove = 3,
}

/// <summary>Forwarded-header policy. A null policy leaves every header untouched.</summary>
public sealed class ForwardedHeadersConfig
{
    public ForwardedHeaderAction XForwardedFor { get; init; }
    public ForwardedHeaderAction XForwardedProto { get; init; }
    public ForwardedHeaderAction XForwardedHost { get; init; }
    public IReadOnlyList<string>? TrustedProxies { get; init; }
}
