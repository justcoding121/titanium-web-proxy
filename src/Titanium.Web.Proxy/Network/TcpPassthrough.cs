using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Network;

/// <summary>How a passthrough relay ended.</summary>
internal enum PassthroughOutcome
{
    /// <summary>Both directions reached EOF.</summary>
    Completed,

    /// <summary>No bytes moved for the idle timeout.</summary>
    IdleTimeout,

    /// <summary>One side closed and the other kept the connection open past the grace period.</summary>
    GraceExpired,

    /// <summary>A socket failed (reset, aborted).</summary>
    Error,

    /// <summary>The proxy was stopping.</summary>
    Cancelled,

    /// <summary>An L4 script filter closed the connection.</summary>
    Denied,
}

/// <summary>Which relay loop a set of options selects. Chosen once per connection, at accept.</summary>
internal enum PassthroughRelayVariant
{
    /// <summary>No timers, no filters. Closes both sockets on the first EOF.</summary>
    Plain,

    /// <summary>Idle or grace timers, or a PROXY preamble.</summary>
    Enhanced,

    /// <summary>Enhanced loop with L4 script filters between the sockets.</summary>
    Filtered,
}

/// <summary>Byte totals and outcome of a relay. Preamble bytes (PROXY header) are not counted.</summary>
internal readonly record struct PassthroughRelayResult(long BytesIn, long BytesOut, PassthroughOutcome Outcome);

/// <summary>Knobs for <see cref="TcpPassthrough" />.</summary>
internal sealed class TcpPassthroughOptions
{
    /// <summary>Optional bytes written to the upstream before anything else (a PROXY protocol header).</summary>
    public ReadOnlyMemory<byte> Preamble { get; set; }

    /// <summary>Bound on the upstream connect. Zero or negative waits for the OS.</summary>
    public TimeSpan ConnectTimeout { get; set; }

    /// <summary>Closes the relay after this long with no bytes in either direction. Zero disables.</summary>
    public TimeSpan IdleTimeout { get; set; }

    /// <summary>
    ///     After one side sends EOF, how long the other direction may keep flowing. Zero closes both sockets on the
    ///     first EOF. With no idle timeout and no preamble that is the cheap legacy relay.
    /// </summary>
    public TimeSpan HalfCloseGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     L4 script filters for this connection, or null. Set at accept only when the listener has filters; a non-null
    ///     value selects the filtered relay, so the plain relay never tests it per read.
    /// </summary>
    internal StreamFilterChain? Filters { get; set; }

    /// <summary>
    ///     True when the relay needs idle or grace timers, or a PROXY preamble. Otherwise the plain relay runs: no
    ///     cancellation sources, and both sockets close on the first EOF.
    /// </summary>
    internal bool NeedsEnhancedRelay =>
        IdleTimeout > TimeSpan.Zero || HalfCloseGrace > TimeSpan.Zero || !Preamble.IsEmpty;

    /// <summary>The relay loop these options select.</summary>
    internal PassthroughRelayVariant Variant =>
        Filters is not null
            ? PassthroughRelayVariant.Filtered
            : NeedsEnhancedRelay
                ? PassthroughRelayVariant.Enhanced
                : PassthroughRelayVariant.Plain;
}

/// <summary>Copies bytes between a client socket and an upstream TCP endpoint.</summary>
internal static class TcpPassthrough
{
    private const int CopyBufferSize = 16 * 1024;

    private static long enhancedRelayCount;

    /// <summary>How many relays took the timer-backed path. The plain path never increments it.</summary>
    internal static long EnhancedRelayCount => Interlocked.Read(ref enhancedRelayCount);

    private static long filteredRelayCount;

    /// <summary>How many relays ran with L4 script filters. Neither other path increments it.</summary>
    internal static long FilteredRelayCount => Interlocked.Read(ref filteredRelayCount);

    /// <summary>
    ///     Connects to <paramref name="host" />:<paramref name="port" /> and relays until the first side ends, then
    ///     closes both sockets.
    /// </summary>
    public static Task RelayAsync(Socket client, string host, int port, CancellationToken cancellationToken) =>
        RelayAsync(
            client, host, port, ReadOnlyMemory<byte>.Empty,
            new TcpPassthroughOptions { HalfCloseGrace = TimeSpan.Zero }, cancellationToken);

    /// <summary>
    ///     Connects, sends the preamble and <paramref name="initialBytes" /> (bytes already read from the client),
    ///     then relays both ways until the connection ends.
    /// </summary>
    public static async Task<PassthroughRelayResult> RelayAsync(
        Socket client, string host, int port, ReadOnlyMemory<byte> initialBytes, TcpPassthroughOptions options,
        CancellationToken cancellationToken)
    {
        using var upstream = await OpenAsync(host, port, initialBytes, options, cancellationToken).ConfigureAwait(false);
        var result = await RelayAsync(client, upstream, options, cancellationToken).ConfigureAwait(false);
        return result with { BytesIn = result.BytesIn + initialBytes.Length };
    }

    /// <summary>
    ///     Connects to the upstream and writes the preamble and <paramref name="initialBytes" />. A failure in any
    ///     of these steps throws and disposes the socket, so the caller can try another destination with the same bytes.
    /// </summary>
    public static async Task<Socket> OpenAsync(
        string host, int port, ReadOnlyMemory<byte> initialBytes, TcpPassthroughOptions options,
        CancellationToken cancellationToken)
    {
        var upstream = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (options.ConnectTimeout > TimeSpan.Zero)
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(options.ConnectTimeout);
                try
                {
                    await upstream.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Connecting to {host}:{port} timed out.");
                }
            }
            else
            {
                await upstream.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            }

            if (!options.Preamble.IsEmpty)
            {
                await SendAllAsync(upstream, options.Preamble, cancellationToken).ConfigureAwait(false);
            }

            if (!initialBytes.IsEmpty)
            {
                await SendAllAsync(upstream, initialBytes, cancellationToken).ConfigureAwait(false);
            }

            return upstream;
        }
        catch
        {
            upstream.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Copies both directions. With no idle timeout, no preamble and no half-close grace, the first EOF closes
    ///     both sockets and nothing else is allocated. Otherwise an EOF is passed on as a send shutdown and the other
    ///     direction keeps flowing for <see cref="TcpPassthroughOptions.HalfCloseGrace" />.
    /// </summary>
    public static Task<PassthroughRelayResult> RelayAsync(
        Socket client, Socket upstream, TcpPassthroughOptions options, CancellationToken cancellationToken) =>
        options.Variant == PassthroughRelayVariant.Plain
            ? RelayPlainAsync(client, upstream, cancellationToken)
            : RelayEnhancedAsync(client, upstream, options, cancellationToken);

    /// <summary>Closes both sockets on the first EOF or failure. No timers, no linked tokens, no shared counters.</summary>
    private static async Task<PassthroughRelayResult> RelayPlainAsync(
        Socket client, Socket upstream, CancellationToken cancellationToken)
    {
        var forward = PlainPumpAsync(client, upstream, cancellationToken);
        var back = PlainPumpAsync(upstream, client, cancellationToken);
        var first = await Task.WhenAny(forward, back).ConfigureAwait(false);
        var firstResult = await first.ConfigureAwait(false);
        try
        {
            client.Close();
            upstream.Close();
        }
        catch (ObjectDisposedException)
        {
            // The peer already closed its side when the first direction finished.
        }

        // Closing the sockets unblocks the other copy. Its failure is the close, not a new failure.
        var forwardResult = await forward.ConfigureAwait(false);
        var backResult = await back.ConfigureAwait(false);

        var outcome = cancellationToken.IsCancellationRequested || firstResult.Error is OperationCanceledException
            ? PassthroughOutcome.Cancelled
            : firstResult.Error is not null
                ? PassthroughOutcome.Error
                : PassthroughOutcome.Completed;
        return new PassthroughRelayResult(forwardResult.Bytes, backResult.Bytes, outcome);
    }

    private readonly record struct PlainPumpResult(long Bytes, Exception? Error);

    private static async Task<PlainPumpResult> PlainPumpAsync(Socket from, Socket to, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer.AsMemory(0, CopyBufferSize), SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return new PlainPumpResult(total, null);
                }

                await SendAllAsync(to, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }
        }
        catch (Exception ex)
        {
            // Reported through the result so one pump's failure never has to be observed as a faulted task.
            return new PlainPumpResult(total, ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<PassthroughRelayResult> RelayEnhancedAsync(
        Socket client, Socket upstream, TcpPassthroughOptions options, CancellationToken cancellationToken)
    {
        var filters = options.Filters;
        if (filters is null)
        {
            Interlocked.Increment(ref enhancedRelayCount);
        }
        else
        {
            Interlocked.Increment(ref filteredRelayCount);
        }

        // The idle and grace sources only exist when their option is set.
        var useIdle = options.IdleTimeout > TimeSpan.Zero;
        var useGrace = options.HalfCloseGrace > TimeSpan.Zero;
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var idleCts = useIdle ? new CancellationTokenSource() : null;
        using var graceCts = useGrace ? new CancellationTokenSource() : null;
        var state = new RelayState(options.IdleTimeout, idleCts);
        using var idleRegistration = idleCts?.Token.Register(
            static s => ((RelayState)s!).Stop(PassthroughOutcome.IdleTimeout), state);
        using var graceRegistration = graceCts?.Token.Register(
            static s => ((RelayState)s!).Stop(PassthroughOutcome.GraceExpired), state);
        state.Cancel = relayCts;
        state.Arm();

        var token = relayCts.Token;
        var forward = PumpAsync(client, upstream, state, toUpstream: true, filters, token);
        var back = PumpAsync(upstream, client, state, toUpstream: false, filters, token);

        var first = await Task.WhenAny(forward, back).ConfigureAwait(false);
        if (first.IsCompletedSuccessfully)
        {
            // Half-close: the other direction may still be draining a response.
            if (graceCts is not null)
            {
                graceCts.CancelAfter(options.HalfCloseGrace);
            }
            else
            {
                state.Stop(PassthroughOutcome.GraceExpired);
            }
        }
        else
        {
            if (first.IsFaulted)
            {
                state.Stop(PassthroughOutcome.Error);
            }
            else
            {
                relayCts.Cancel();
            }
        }

        try
        {
            await Task.WhenAll(forward, back).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closing or cancelling one side unblocks the other. Which one failed first decides the outcome below.
        }

        var outcome = state.Reason;
        if (outcome == PassthroughOutcome.Completed)
        {
            if (forward.IsFaulted || back.IsFaulted)
            {
                outcome = PassthroughOutcome.Error;
            }
            else if (forward.IsCanceled || back.IsCanceled)
            {
                outcome = PassthroughOutcome.Cancelled;
            }
        }

        return new PassthroughRelayResult(state.BytesIn, state.BytesOut, outcome);
    }

    private static async Task PumpAsync(
        Socket from, Socket to, RelayState state, bool toUpstream, StreamFilterChain? filters,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer.AsMemory(0, CopyBufferSize), SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                state.Arm();
                var data = buffer.AsMemory(0, read);
                if (filters is not null)
                {
                    var verdict = await filters.OnDataAsync(toUpstream, data, cancellationToken).ConfigureAwait(false);
                    if (verdict.Action == Abstractions.Scripting.ScriptAction.Deny)
                    {
                        state.Stop(PassthroughOutcome.Denied);
                        break;
                    }

                    if (verdict.ReplaceData is not null)
                    {
                        data = verdict.ReplaceData;
                    }
                }

                await SendAllAsync(to, data, cancellationToken).ConfigureAwait(false);
                total += read;
                state.Publish(toUpstream, total);
                state.Arm();
            }
        }
        catch
        {
            // A reset or abort on one side must not leave the other waiting on a peer that will never answer.
            state.Fail(cancellationToken);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        TryShutdownSend(to);
    }

    private static async ValueTask SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        while (!data.IsEmpty)
        {
            var sent = await socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            data = data[sent..];
        }
    }

    private static void TryShutdownSend(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // The peer already reset or closed. There is no one left to tell.
        }
        catch (ObjectDisposedException)
        {
            // Closed during teardown.
        }
    }

    private sealed class RelayState
    {
        private readonly long idleMilliseconds;
        private readonly long rearmMilliseconds;
        private readonly CancellationTokenSource? idle;
        private long armedAt;

        // Each counter has one writer (its own pump), so a plain write is enough. They are read after both pumps end.
        private long bytesIn;
        private long bytesOut;
        private int reason = -1;

        public RelayState(TimeSpan idleTimeout, CancellationTokenSource? idleSource)
        {
            idle = idleSource;
            idleMilliseconds = idleSource is not null && idleTimeout > TimeSpan.Zero
                ? (long)Math.Min(idleTimeout.TotalMilliseconds, int.MaxValue - 1)
                : 0;

            // Re-arming a timer on every 16 KiB chunk is wasted work. A coarse refresh is exact enough for a timeout.
            rearmMilliseconds = Math.Clamp(idleMilliseconds / 8, 1, 1000);
        }

        public CancellationTokenSource? Cancel { get; set; }

        public long BytesIn => Volatile.Read(ref bytesIn);

        public long BytesOut => Volatile.Read(ref bytesOut);

        public PassthroughOutcome Reason =>
            Volatile.Read(ref reason) is var r and >= 0 ? (PassthroughOutcome)r : PassthroughOutcome.Completed;

        public void Arm()
        {
            if (idle is null || idleMilliseconds == 0)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (Volatile.Read(ref armedAt) != 0 && now - Volatile.Read(ref armedAt) < rearmMilliseconds)
            {
                return;
            }

            Volatile.Write(ref armedAt, now);
            idle.CancelAfter((int)idleMilliseconds);
        }

        /// <summary>Records a direction's running total. Only that direction's pump calls it.</summary>
        public void Publish(bool toUpstream, long total)
        {
            if (toUpstream)
            {
                Volatile.Write(ref bytesIn, total);
            }
            else
            {
                Volatile.Write(ref bytesOut, total);
            }
        }

        public void Stop(PassthroughOutcome why)
        {
            Interlocked.CompareExchange(ref reason, (int)why, -1);
            try
            {
                Cancel?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The relay already finished.
            }
        }

        /// <summary>A pump faulted. Cancellation caused by this relay or the proxy stopping is not a failure.</summary>
        public void Fail(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            Stop(PassthroughOutcome.Error);
        }
    }
}
