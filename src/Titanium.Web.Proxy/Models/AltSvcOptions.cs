using System.Diagnostics.CodeAnalysis;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     Client-facing <c>Alt-Svc</c> advertisement for a dual-listen HTTP/3 endpoint.
///     <see langword="null" /> on the endpoint keeps today's header (<c>h3=":port"; ma=86400</c>)
///     whenever <see cref="TransparentProxyEndPoint.EnableHttp3" /> is on.
///     Assign a new instance to apply changes; the endpoint caches the header text when this object is assigned.
/// </summary>
[Experimental("TWP001")]
public sealed class AltSvcOptions
{
    /// <summary>
    ///     When <see langword="false" />, the proxy never injects <c>Alt-Svc</c>.
    ///     Default: <see langword="true" />.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Overrides the <c>ma</c> parameter (seconds). Null uses 86400.
    /// </summary>
    public int? MaxAgeSeconds { get; set; }

    /// <summary>
    ///     Overrides the advertised UDP port. Null uses the endpoint's bound port.
    /// </summary>
    public int? Port { get; set; }
}
