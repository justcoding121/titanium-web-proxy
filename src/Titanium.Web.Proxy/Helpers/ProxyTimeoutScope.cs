using System;
using System.Threading;
using Titanium.Web.Proxy.Exceptions;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Linked <see cref="CancellationTokenSource" /> with <see cref="CancellationTokenSource.CancelAfter(TimeSpan)" />
///     for a single deadline. Dispose in a finally block. When the deadline fires but the parent
///     session token was not cancelled, <see cref="ThrowIfTimedOut" /> raises
///     <see cref="ProxyTimeoutException" /> with the configured <see cref="Kind" />.
/// </summary>
internal sealed class ProxyTimeoutScope : IDisposable
{
    private readonly CancellationToken parentToken;
    private readonly CancellationTokenSource? linkedCts;
    private bool disposed;

    private ProxyTimeoutScope(CancellationTokenSource? linkedCts, CancellationToken parentToken,
        CancellationToken token, ProxyTimeoutKind kind)
    {
        this.linkedCts = linkedCts;
        this.parentToken = parentToken;
        Token = token;
        Kind = kind;
    }

    /// <summary>
    ///     Token to pass into the timed operation (parent token when no deadline is active).
    /// </summary>
    public CancellationToken Token { get; }

    /// <summary>
    ///     Timeout kind reported when this scope's deadline elapses.
    /// </summary>
    public ProxyTimeoutKind Kind { get; }

    /// <summary>
    ///     True when a positive deadline was applied.
    /// </summary>
    public bool HasDeadline => linkedCts != null;

    /// <summary>
    ///     Creates a scope. When <paramref name="timeout" /> is null or non-positive, returns a
    ///     no-op scope that just forwards <paramref name="parentToken" />.
    /// </summary>
    public static ProxyTimeoutScope Create(CancellationToken parentToken, TimeSpan? timeout,
        ProxyTimeoutKind kind)
    {
        if (timeout is not { } deadline || deadline <= TimeSpan.Zero)
            return new ProxyTimeoutScope(null, parentToken, parentToken, kind);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        linked.CancelAfter(deadline);
        return new ProxyTimeoutScope(linked, parentToken, linked.Token, kind);
    }

    /// <summary>
    ///     If <paramref name="exception" /> (or an inner exception) is cancellation caused by this
    ///     scope's deadline — and not by the parent session token — throws
    ///     <see cref="ProxyTimeoutException" />. Otherwise rethrows the original exception.
    /// </summary>
    public void ThrowIfTimedOut(Exception exception)
    {
        if (IsTimedOut())
        {
            throw new ProxyTimeoutException(
                $"Proxy {Kind.ToString().ToLowerInvariant()} timeout elapsed.",
                Kind, exception);
        }

        if (exception is OperationCanceledException)
            throw exception;

        throw exception;
    }

    /// <summary>
    ///     Returns true when this scope's deadline cancelled the linked token while the parent
    ///     session token is still open.
    /// </summary>
    public bool IsTimedOut()
    {
        return linkedCts != null
               && linkedCts.IsCancellationRequested
               && !parentToken.IsCancellationRequested;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        linkedCts?.Dispose();
    }
}
