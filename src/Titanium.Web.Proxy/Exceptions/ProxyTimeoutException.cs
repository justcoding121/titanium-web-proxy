using System;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Thrown when a configured proxy timeout elapses. Surfaced through
///     <see cref="Logging.ProxyDiagnostics" /> so callers can distinguish connect,
///     response-header, idle, and total request deadlines.
/// </summary>
public class ProxyTimeoutException : ProxyException
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProxyTimeoutException" /> class.
    /// </summary>
    /// <param name="message">Exception message.</param>
    /// <param name="kind">Which timeout elapsed.</param>
    /// <param name="innerException">Optional inner exception (typically <see cref="OperationCanceledException" />).</param>
    public ProxyTimeoutException(string message, ProxyTimeoutKind kind, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>
    ///     Which configured timeout elapsed.
    /// </summary>
    public ProxyTimeoutKind Kind { get; }
}
