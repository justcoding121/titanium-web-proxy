using System;
using System.Net;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Thrown by <see cref="Titanium.Web.Proxy.Http.Http1FramingValidator" /> when a wire-parsed HTTP/1
///     message's framing is ambiguous or names an unsupported transfer coding. Carries the status code
///     RFC 9112 assigns to the failure - 400 for ambiguous <c>Content-Length</c>/<c>Transfer-Encoding</c>
///     framing, 501 for a transfer coding this proxy does not implement (RFC 9112 §6.1) - so the catching
///     handler can report it to the affected peer without re-deriving which status applies.
///     <para>
///         Callers must treat the connection the ambiguous message arrived on as poisoned: once framing
///         is ambiguous, the reader and the peer no longer agree on where the message ends, so the
///         connection must be closed rather than kept alive or returned to a pool.
///     </para>
/// </summary>
internal sealed class Http1FramingException : Exception
{
    public Http1FramingException(string message, HttpStatusCode statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The status code to report to the peer that sent the offending message.</summary>
    public HttpStatusCode StatusCode { get; }
}
