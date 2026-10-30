using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
///     Best-effort mirror owned by one <see cref="ProxyServer" />. A full queue increments the drop
///     counter. The copy is a separate connection and never writes the client's response.
///     Each route has its own queue and workers, so a slow mirror cluster cannot starve another route.
///     Buffered memory is bounded per route by 32 queued bodies of at most
///     <see cref="MirrorConfig.MaxAllowedBodyBytes" />.
///     <para>
///         Upstream TLS: a mirror copy ignores the cluster's <c>httpClient</c> TLS settings (client certificate,
///         CA bundle, <c>serverName</c>, <c>verifyHostname</c>, <c>acceptAnyServerCertificate</c>). It dials with
///         the machine trust store and the destination address as the TLS name. A mirror cluster that needs a
///         private CA or mTLS therefore fails its copy (counted as failed) and never affects the client's response.
///         This is deliberate: the copy is best effort and must not widen what the proxy trusts.
///     </para>
/// </summary>
internal sealed class MirrorDispatcher : IDisposable
{
    private const int MaxQueued = 32;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, Lane> lanes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationToken shutdownToken;
    private int queued;
    private int dropped;
    private int skipped;
    private int sent;
    private int failed;
    private int holdEntries;
    private int disposed;

    public MirrorDispatcher() => shutdownToken = shutdown.Token;

    /// <summary>Test sink. When set, nothing is queued.</summary>
    internal Action<string, string, string>? Observed { get; set; }

    /// <summary>Replaces the socket send. Tests use it so they do not need a listener.</summary>
    internal Func<MirrorWork, Task>? Sender { get; set; }

    internal int QueuedForTests => queued;
    internal int DroppedForTests => dropped;
    internal int SkippedForTests => skipped;
    internal int SentForTests => sent;
    internal int FailedForTests => failed;
    internal int HoldEntriesForTests => holdEntries;

    /// <summary>How long a route's idle worker waits for work before it exits. The next copy starts it again.</summary>
    internal TimeSpan IdleExit { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Workers running across all routes.</summary>
    internal int WorkerCountForTests => lanes.Values.Sum(static lane => Volatile.Read(ref lane.Running));

    /// <summary>When set, a worker waits before sending. Tests use it to fill the queue.</summary>
    internal Task? HoldForTests { get; set; }

    internal void ResetForTests()
    {
        Observed = null;
        Sender = null;
        HoldForTests = null;
        foreach (var lane in lanes.Values)
        {
            while (lane.Queue.Reader.TryRead(out _))
            {
                // Drop leftover copies. The test is resetting the dispatcher, not delivering them.
            }
        }

        queued = 0;
        dropped = 0;
        skipped = 0;
        sent = 0;
        failed = 0;
        holdEntries = 0;
    }

    /// <param name="routeId">Names the queue and worker limit. Defaults to the mirror cluster id.</param>
    public void TryEnqueue(MirrorConfig mirror, Request request, DestinationConfig? destination = null, string? routeId = null)
    {
        if (Volatile.Read(ref disposed) != 0 || mirror.SamplePercent <= 0)
        {
            return;
        }

        if (mirror.SamplePercent < 100 && Random.Shared.Next(100) >= mirror.SamplePercent)
        {
            return;
        }

        var path = request.RequestUriString8.GetString();
        if (Observed is { } observed)
        {
            observed(mirror.ClusterId, request.Method ?? "GET", path);
            return;
        }

        // The copy is written as raw text. A request line or Host value with a space or line break would
        // split into a second request on the mirror, so such a request is skipped rather than rewritten.
        if (destination is null || destination.Address.StartsWith("unix:", StringComparison.OrdinalIgnoreCase) ||
            !IsSafeRequestLine(request.Method ?? "GET", path, request.Host) ||
            !TryCopyBody(mirror, request, out var body))
        {
            Interlocked.Increment(ref skipped);
            ProxyMetrics.RecordMirrorSkipped();
            return;
        }

        int port;
        if (destination.Port == 0)
        {
            port = destination.UseHttps ? 443 : 80;
        }
        else
        {
            port = destination.Port;
        }
        var work = new MirrorWork(
            destination.Address,
            port,
            destination.UseHttps,
            request.Method ?? "GET",
            path,
            request.Host ?? destination.Address,
            CopyHeaders(request),
            body);

        var lane = lanes.GetOrAdd(routeId ?? mirror.ClusterId, static _ => new Lane());
        if (!lane.Queue.Writer.TryWrite(work))
        {
            Interlocked.Increment(ref dropped);
            ProxyMetrics.RecordMirrorDrop();
            return;
        }

        Interlocked.Increment(ref queued);
        ProxyMetrics.RecordMirrorQueued();
        EnsureWorkers(lane, mirror.MaxConcurrency);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // A mirror copy is best effort, so there is nothing to wait for. Cancelling ends an open send
        // at once, and the workers exit on their own once their queue is empty and complete.
        shutdown.Cancel();
        foreach (var queue in lanes.Values.Select(static lane => lane.Queue))
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _))
            {
                // Copies that never started are dropped with the proxy.
            }
        }

        shutdown.Dispose();
    }

    private void EnsureWorkers(Lane lane, int requested)
    {
        var target = Math.Clamp(requested <= 0 ? 2 : requested, 1, 16);
        lock (lane.Gate)
        {
            lane.Tasks.RemoveAll(static task => task.IsCompleted);
            while (lane.Running < target && Volatile.Read(ref disposed) == 0)
            {
                lane.Running++;
                lane.Tasks.Add(Task.Run(() => WorkerAsync(lane), shutdownToken));
            }
        }
    }

    /// <summary>
    ///     Ends a worker that has been idle. Under the gate so a copy written just before this call
    ///     is seen here, and a copy written after it finds the worker gone and starts another.
    /// </summary>
    private bool TryRetire(Lane lane)
    {
        lock (lane.Gate)
        {
            if (Volatile.Read(ref disposed) == 0 && lane.Queue.Reader.TryPeek(out _))
            {
                return false;
            }

            lane.Running--;
            return true;
        }
    }

    private async Task<bool> WaitForWorkAsync(Lane lane)
    {
        using var idle = new CancellationTokenSource(IdleExit);
        try
        {
            return await lane.Queue.Reader.WaitToReadAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static bool TryCopyBody(MirrorConfig mirror, Request request, out byte[]? body)
    {
        body = null;
        if (!request.HasBody)
        {
            return true;
        }

        var cap = mirror.MaxBodyBytes > 0 ? Math.Min(mirror.MaxBodyBytes, MirrorConfig.MaxAllowedBodyBytes) : 65536;
        if (!request.IsBodyRead || request.ContentLength > cap)
        {
            return false;
        }

        body = request.Body;
        return body.Length <= cap;
    }

    private static bool IsSafeRequestLine(string method, string path, string? host) =>
        method.Length > 0 && path.Length > 0 &&
        !HasControlOrSpace(method) && !HasControlOrSpace(path) && !HasLineBreak(host);

    private static bool HasControlOrSpace(string value) =>
        value.Any(static ch => ch <= ' ' || ch == 0x7f);

    private static bool HasLineBreak(string? value) =>
        value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0;

    private static string CopyHeaders(Request request)
    {
        var block = new StringBuilder();
        foreach (var header in request.Headers)
        {
            if (IsHopByHop(header.Name) || HasLineBreak(header.Name) || HasLineBreak(header.Value))
            {
                continue;
            }

            block.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        return block.ToString();
    }

    private static bool IsHopByHop(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Expect", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase);

    private async Task WorkerAsync(Lane lane)
    {
        while (true)
        {
            if (!await WaitForWorkAsync(lane).ConfigureAwait(false))
            {
                if (TryRetire(lane))
                {
                    return;
                }

                continue;
            }

            if (HoldForTests is { } hold)
            {
                Interlocked.Increment(ref holdEntries);
                await hold.ConfigureAwait(false);
            }

            while (lane.Queue.Reader.TryRead(out var work))
            {
                await DeliverAsync(work).ConfigureAwait(false);
            }
        }
    }

    private async Task DeliverAsync(MirrorWork work)
    {
        try
        {
            if (Sender is { } sender)
            {
                await sender(work).ConfigureAwait(false);
            }
            else
            {
                await SendAsync(work, shutdownToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref sent);
            ProxyMetrics.RecordMirrorSent();
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disposed) != 0)
        {
            // The proxy is stopping. A cancelled copy is not a failed one.
        }
        catch
        {
            Interlocked.Increment(ref failed);
            ProxyMetrics.RecordMirrorFailed();
        }
    }

    private static async Task SendAsync(MirrorWork work, CancellationToken stopping)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(SendTimeout);
        await client.ConnectAsync(work.Host, work.Port, timeout.Token).ConfigureAwait(false);
        Stream stream = client.GetStream();
        if (work.UseHttps)
        {
            var secure = new SslStream(stream, false);
            stream = secure;
            await secure.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = work.Host }, timeout.Token).ConfigureAwait(false);
        }

        await using var closer = stream.ConfigureAwait(false);
        var head = work.Method + " " + work.Path + " HTTP/1.1\r\nHost: " + work.HostHeader +
                   "\r\nConnection: close\r\n" + work.Headers;
        if (work.Body is { Length: > 0 } body)
        {
            head += "Content-Length: " + body.Length + "\r\n\r\n";
        }
        else
        {
            head += "\r\n";
        }

        var payload = Encoding.UTF8.GetBytes(head);
        if (work.Body is { Length: > 0 } bytes)
        {
            var combined = new byte[payload.Length + bytes.Length];
            payload.CopyTo(combined, 0);
            bytes.CopyTo(combined, payload.Length);
            payload = combined;
        }

        await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        var sink = new byte[256];
        while (await stream.ReadAsync(sink, timeout.Token).ConfigureAwait(false) > 0)
        {
            // The mirror does not use the origin's response. Drain it so the origin can finish.
        }
    }

    /// <summary>One route's queue and workers.</summary>
    private sealed class Lane
    {
        internal readonly Channel<MirrorWork> Queue = Channel.CreateBounded<MirrorWork>(new BoundedChannelOptions(MaxQueued)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
        });

        internal readonly object Gate = new();
        internal readonly List<Task> Tasks = new();
        internal int Running;
    }

    internal readonly record struct MirrorWork(
        string Host, int Port, bool UseHttps, string Method, string Path, string HostHeader, string Headers, byte[]? Body);
}
