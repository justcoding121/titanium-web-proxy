using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Thrown when an HTTP upstream proxy rejects a CONNECT tunnel (or otherwise fails to
///     establish one) with a non-success response. Carries the upstream status, headers, and a
///     bounded body snapshot for diagnostics. Relaying that response to the client is only safe
///     before the client-facing CONNECT 200 has been committed — enable
///     <see cref="EventArguments.TunnelConnectSessionEventArgs.EstablishServerConnectionBeforeResponse" />
///     and handle <see cref="Models.ExplicitProxyEndPoint.BeforeTunnelConnectFailure" /> (issue #768).
/// </summary>
public class UpstreamProxyConnectException : ProxyException
{
    internal UpstreamProxyConnectException(string message, int statusCode, string statusDescription,
        IReadOnlyDictionary<string, string> headers, string? bodyPreview = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        StatusDescription = statusDescription ?? string.Empty;
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BodyPreview = bodyPreview;
    }

    /// <summary>
    ///     Upstream HTTP status code (for example 403 or 407).
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    ///     Upstream reason phrase.
    /// </summary>
    public string StatusDescription { get; }

    /// <summary>
    ///     Snapshot of upstream response headers (single value per header name).
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    ///     Bounded UTF-8 preview of the upstream response body, if any was present.
    /// </summary>
    public string? BodyPreview { get; }
}
