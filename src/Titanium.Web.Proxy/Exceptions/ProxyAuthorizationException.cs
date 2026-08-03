using System;
using System.Collections.Generic;
using System.Linq;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Proxy authorization exception.
/// </summary>
public class ProxyAuthorizationException : ProxyException
{
    private const string RedactedValue = "[REDACTED]";

    /// <summary>
    ///     Initializes a new instance of the <see cref="ProxyAuthorizationException" /> class.
    /// </summary>
    /// <param name="message">Exception message.</param>
    /// <param name="session">The <see cref="SessionEventArgs" /> instance containing the event data.</param>
    /// <param name="innerException">Inner exception associated to upstream proxy authorization</param>
    /// <param name="headers">Http's headers associated</param>
    /// <remarks>
    ///     <see cref="Headers" /> is a public, user-visible property that callers commonly log or
    ///     forward to crash reporting - it must never carry the plaintext credential/token that
    ///     <c>Authorization</c>/<c>Proxy-Authorization</c> hold, or an <c>OnException</c> handler that
    ///     just calls <c>ex.ToString()</c> (or serializes the exception) would leak them.
    /// </remarks>
    internal ProxyAuthorizationException(string message, SessionEventArgsBase session, Exception innerException,
        IEnumerable<HttpHeader> headers) : base(message, innerException)
    {
        Session = session;
        Headers = Redact(headers);
    }

    private static List<HttpHeader> Redact(IEnumerable<HttpHeader> headers) =>
        headers.Select(h =>
            KnownHeaders.Authorization.Equals(h.Name) || KnownHeaders.ProxyAuthorization.Equals(h.Name)
                ? new HttpHeader(h.Name, RedactedValue)
                : h).ToList();

    /// <summary>
    ///     The current session within which this error happened.
    /// </summary>
    public SessionEventArgsBase Session { get; }

    /// <summary>
    ///     Headers associated with the authorization exception.
    /// </summary>
    public IEnumerable<HttpHeader> Headers { get; }
}