namespace Titanium.Web.Proxy.Models;

/// <summary>
///     Controls which HTTP version the proxy declares to the origin server on the request line, independently of
///     the version the client itself declared on its own connection to the proxy.
///     <para>
///         HTTP/1.0 and HTTP/1.1 share the same start-line/header/body wire format, so switching between them
///         needs no message translation - only the declared version and the resulting default persistence
///         (<see cref="Titanium.Web.Proxy.Http.Response.KeepAlive" />) change. The response is always written
///         back to the client using the client's own originally declared version and its own persistence rules,
///         regardless of this policy.
///     </para>
/// </summary>
public enum OriginHttpVersionPolicy
{
    /// <summary>
    ///     Declare the same HTTP version to the origin that the client declared to the proxy (default; matches
    ///     the proxy's historical pass-through behavior). An HTTP/1.0 client therefore also causes an HTTP/1.0
    ///     declaration to the origin, and - per RFC 2616 §8.1 - a compliant origin's default-non-persistent
    ///     HTTP/1.0 response then prevents that origin connection from being pooled/reused.
    /// </summary>
    PreserveClientVersion,

    /// <summary>
    ///     Always declare HTTP/1.1 to the origin, regardless of what version the client declared. A compliant
    ///     origin can then be treated as persistent by default and its connection pooled/reused across requests
    ///     - including requests from HTTP/1.0 clients that would otherwise never be able to share a pooled origin
    ///     connection with HTTP/1.1 clients of the same origin.
    /// </summary>
    NormalizeToHttp11
}
