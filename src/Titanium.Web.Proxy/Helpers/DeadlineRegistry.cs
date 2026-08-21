using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Exceptions;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Owns every <see cref="ProxyTimeoutKind" /> deadline composed for a single request/session and
///     records, independent of whether the <see cref="Deadline" /> scope that owned it has since been
///     disposed by stack unwinding, which one actually elapsed first.
///     <para>
///         <see cref="ProxyTimeoutScope" /> attributes a firing by comparing a scope's own linked token
///         against its immediate parent at the moment some <em>later</em> code asks
///         <c>IsTimedOut()</c> - which only works when that check runs before the scope is disposed.
///         Once an inner scope's <c>using</c> block has already unwound (its <c>Dispose</c> ran during
///         exception propagation, before an outer, un-nested catch gets a chance to inspect anything),
///         there is nothing left to compare: the inner scope's tokens are gone. Composing several
///         deadlines - client-header, then request, then idle-write, potentially several layers of
///         plain <c>await</c> apart with no per-layer <c>try/catch</c> in between - needs the moment of
///         firing recorded no later than the moment the scope itself is torn down, not reconstructed
///         later from state that may no longer exist.
///     </para>
///     <para>
///         Each catch site calls <see cref="Deadline.TryGetTimeoutException" /> on its own, still-live
///         <see cref="Deadline" />: that checks this deadline's own "mine, not my parent's" condition -
///         the same test <see cref="ProxyTimeoutScope" /> performs - synchronously, before falling back
///         to this registry for a firing already recorded by some other (necessarily inner, thus
///         necessarily already-disposed) deadline that shares it. An earlier design instead registered a
///         <see cref="CancellationToken.Register(Action{object},object)" /> callback to record firings
///         asynchronously off of the token's own cancellation, independently of any catch site. That is
///         actively wrong: <see cref="CancellationTokenSource.Cancel" /> invokes every callback registered
///         against a token in LIFO order on the thread that observes the timeout, and a callback
///         registered later than ours (e.g. deep inside a socket-read helper's own internal
///         <c>WithCancellation</c>-style wrapper) can synchronously resume its awaiter inline - running
///         that awaiter all the way through this deadline's <c>catch</c> and <c>using</c> disposal -
///         before <c>Cancel</c>'s loop ever reaches our callback, disposing our
///         <see cref="CancellationTokenRegistration" /> and permanently starving it. Recording via a
///         direct, synchronous check at each catch site (and via <see cref="Deadline.Dispose" /> for
///         scopes that unwind without ever being asked) ties recording to points that are guaranteed to
///         run, on the thread that is already there, with no callback queue to race against.
///     </para>
/// </summary>
internal sealed class DeadlineRegistry
{
    private readonly object gate = new();
    private ProxyTimeoutKind? firedKind;
    private long firedTimestamp;
    private Deadline? passthrough0;
    private Deadline? passthrough1;
    private int passthroughInUse;

    /// <summary>
    ///     Starts a new deadline against <paramref name="parentToken" />. Dispose the returned scope in a
    ///     <c>finally</c>/<c>using</c> block once the bounded operation completes.
    ///     When <paramref name="timeout"/> is null or non-positive (all probe defaults), reuses up to two
    ///     cached scopes so keep-alive GETs do not allocate a <see cref="Deadline"/> per Start.
    /// </summary>
    public Deadline Start(CancellationToken parentToken, TimeSpan? timeout, ProxyTimeoutKind kind) // NOSONAR CA1068 -- Parameter order is retained to avoid churn across deadline call sites.
    {
        if (timeout is not { } d || d <= TimeSpan.Zero)
        {
            if (passthroughInUse < 2)
            {
                var index = passthroughInUse++;
                ref var slot = ref index == 0 ? ref passthrough0 : ref passthrough1;
                if (slot == null)
                    slot = new Deadline(this, parentToken, null, kind, cachedPassthrough: true);
                else
                    slot.ReusePassthrough(parentToken, kind);
                return slot;
            }
        }

        return new Deadline(this, parentToken, timeout, kind, cachedPassthrough: false);
    }

    /// <summary>
    ///     Clears a prior firing so this registry can be reused for the next keep-alive request
    ///     on the same client connection (avoids allocating a new registry per GET).
    /// </summary>
    internal void Reset()
    {
        lock (gate)
        {
            firedKind = null;
            firedTimestamp = 0;
        }
    }

    /// <summary>
    ///     True if any deadline owned by this registry has already been attributed (via
    ///     <see cref="Deadline.TryGetTimeoutException" /> or <see cref="Deadline.Dispose" />), and if so,
    ///     the earliest one by actual elapsed-wall-clock order.
    /// </summary>
    public bool TryGetFiredKind(out ProxyTimeoutKind kind)
    {
        lock (gate)
        {
            if (firedKind is { } k)
            {
                kind = k;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private void Record(ProxyTimeoutKind kind, long timestamp)
    {
        var firstRecordForThisRegistry = false;
        lock (gate)
        {
            if (firedKind == null || timestamp < firedTimestamp)
            {
                firstRecordForThisRegistry = firedKind == null;
                firedKind = kind;
                firedTimestamp = timestamp;
            }
        }

        // Only the first firing recorded against this registry corresponds to a deadline that
        // actually elapsed; later, earlier-timestamped corrections (an inner deadline unwinding
        // after this call already recorded an outer one) re-attribute the same single real event
        // to a different kind rather than describing a second timeout.
        if (firstRecordForThisRegistry) ProxyMetrics.TimeoutFired(kind.ToString());
    }

    /// <summary>
    ///     A single active deadline, linked from some parent token. Mirrors <see cref="ProxyTimeoutScope" />'s
    ///     public shape (<see cref="Token" />, <see cref="HasDeadline" />) so call sites read the same way;
    ///     the difference is entirely in how a firing is attributed (see <see cref="DeadlineRegistry" />'s
    ///     remarks).
    /// </summary>
    public sealed class Deadline : IDisposable
    {
        private readonly DeadlineRegistry registry;
        private readonly bool cachedPassthrough;
        private CancellationToken parentToken;
        private readonly CancellationTokenSource? linkedCts;
        private bool disposed;

        internal Deadline(DeadlineRegistry registry, CancellationToken parentToken, TimeSpan? timeout, // NOSONAR CA1068 -- Constructor mirrors Start parameter order.
            ProxyTimeoutKind kind, bool cachedPassthrough = false)
        {
            this.registry = registry;
            this.cachedPassthrough = cachedPassthrough;
            this.parentToken = parentToken;
            Kind = kind;

            if (timeout is not { } deadline || deadline <= TimeSpan.Zero)
            {
                Token = parentToken;
                return;
            }

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            linkedCts.CancelAfter(deadline);
            Token = linkedCts.Token;
        }

        internal void ReusePassthrough(CancellationToken parent, ProxyTimeoutKind kind)
        {
            parentToken = parent;
            Token = parent;
            Kind = kind;
            disposed = false;
        }

        /// <summary>Token to pass into the timed operation (the parent token when no deadline is active).</summary>
        public CancellationToken Token { get; private set; }

        /// <summary>Timeout kind attributed if this deadline's own timer elapses.</summary>
        public ProxyTimeoutKind Kind { get; private set; }

        /// <summary>True when a positive deadline was actually applied.</summary>
        public bool HasDeadline => linkedCts != null;

        /// <summary>
        ///     True when this deadline's own timer - not an ancestor's - is what elapsed. Mirrors
        ///     <see cref="ProxyTimeoutScope.IsTimedOut" />; evaluated fresh every call rather than cached,
        ///     since it must stay correct if some other deadline was attributed first (see
        ///     <see cref="TryGetTimeoutException" />).
        /// </summary>
        private bool IsTimedOut => linkedCts is { IsCancellationRequested: true } &&
                                    !parentToken.IsCancellationRequested;

        /// <summary>
        ///     Call from the <c>catch</c> immediately guarding this deadline's operation, while it is
        ///     still alive (i.e. before its <c>using</c> disposes it). Checks, in order: whether this
        ///     deadline's own timer is what elapsed; failing that, whether some other deadline sharing
        ///     this registry already recorded a firing (necessarily an inner one, since it would have
        ///     had to already run its own check-and-dispose while unwinding through this frame to reach
        ///     here). Returns <see langword="false" /> - with <paramref name="timeoutException" /> null -
        ///     when neither holds, so the caller can rethrow <paramref name="original" /> as-is.
        /// </summary>
        public bool TryGetTimeoutException(Exception original, out ProxyTimeoutException? timeoutException)
        {
            if (IsTimedOut)
            {
                registry.Record(Kind, Stopwatch.GetTimestamp());
                timeoutException =
                    new ProxyTimeoutException($"Proxy {Kind.ToString().ToLowerInvariant()} timeout elapsed.", Kind,
                        original);
                return true;
            }

            if (registry.TryGetFiredKind(out var kind))
            {
                timeoutException =
                    new ProxyTimeoutException($"Proxy {kind.ToString().ToLowerInvariant()} timeout elapsed.", kind,
                        original);
                return true;
            }

            timeoutException = null;
            return false;
        }

        /// <summary>
        ///     Convenience wrapper around <see cref="TryGetTimeoutException" /> for call sites that always
        ///     want to throw either way: a typed <see cref="ProxyTimeoutException" /> if attributable, or
        ///     <paramref name="original" /> itself - with its original stack trace preserved via
        ///     <see cref="ExceptionDispatchInfo" />, unlike a bare <c>throw original;</c>, which would
        ///     reset it to this rethrow point - otherwise.
        /// </summary>
        public void ThrowIfTimedOut(Exception original)
        {
            if (TryGetTimeoutException(original, out var timeoutException)) throw timeoutException!;
            ExceptionDispatchInfo.Capture(original).Throw();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            // Covers the case where this scope unwinds via a plain await with no catch of its own in
            // between (the scenario DeadlineRegistry exists for) - checked and recorded synchronously
            // here, on whatever thread is unwinding this scope, rather than via an async
            // CancellationToken.Register callback. See the type-level remarks on DeadlineRegistry.
            if (IsTimedOut) registry.Record(Kind, Stopwatch.GetTimestamp());

            linkedCts?.Dispose();
            if (cachedPassthrough && registry.passthroughInUse > 0)
                registry.passthroughInUse--;
        }
    }
}
