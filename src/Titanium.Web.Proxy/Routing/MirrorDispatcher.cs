using System;
using System.Collections.Generic;
using System.IO;
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
///     Buffered memory is bounded by 32 queued bodies of at most <see cref="MirrorConfig.MaxBodyBytes" />.
/// </summary>
internal sealed class MirrorDispatcher : IDisposable
{
    private const int MaxQueued = 32;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);
    private readonly Channel<MirrorWork> queue = Channel.CreateBounded<MirrorWork>(new BoundedChannelOptions(MaxQueued)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
    });

    private readonly List<Task> workers = new();
    private readonly object workerGate = new();
    private int queued;
    private int dropped;
    private int skipped;
    private int sent;
    private int failed;
    private int holdEntries;
    private int disposed;

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

    /// <summary>When set, a worker waits before sending. Tests use it to fill the queue.</summary>
    internal Task? HoldForTests { get; set; }

    internal void ResetForTests()
    {
        Observed = null;
        Sender = null;
        HoldForTests = null;
        while (queue.Reader.TryRead(out _))
        {
            // Drop leftover copies. The test is resetting the dispatcher, not delivering them.
        }

        queued = 0;
        dropped = 0;
        skipped = 0;
        sent = 0;
        failed = 0;
        holdEntries = 0;
    }

    public void TryEnqueue(MirrorConfig mirror, Request request, DestinationConfig? destination = null)
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

        if (destination is null || destination.Address.StartsWith("unix:", StringComparison.OrdinalIgnoreCase) ||
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

        if (!queue.Writer.TryWrite(work))
        {
            Interlocked.Increment(ref dropped);
            ProxyMetrics.RecordMirrorDrop();
            return;
        }

        Interlocked.Increment(ref queued);
        ProxyMetrics.RecordMirrorQueued();
        EnsureWorkers(mirror.MaxConcurrency);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        queue.Writer.TryComplete();
        Task[] running;
        lock (workerGate)
        {
            running = workers.ToArray();
        }

        if (running.Length == 0)
        {
            return;
        }

        Task.WhenAll(running).Wait(TimeSpan.FromSeconds(1));
    }

    private void EnsureWorkers(int requested)
    {
        var target = Math.Clamp(requested <= 0 ? 2 : requested, 1, 16);
        lock (workerGate)
        {
            while (workers.Count < target && Volatile.Read(ref disposed) == 0)
            {
                workers.Add(Task.Run(WorkerAsync));
            }
        }
    }

    private static bool TryCopyBody(MirrorConfig mirror, Request request, out byte[]? body)
    {
        body = null;
        if (!request.HasBody)
        {
            return true;
        }

        var cap = mirror.MaxBodyBytes > 0 ? mirror.MaxBodyBytes : 65536;
        if (!request.IsBodyRead || request.ContentLength > cap)
        {
            return false;
        }

        body = request.Body;
        return body.Length <= cap;
    }

    private static string CopyHeaders(Request request)
    {
        var block = new StringBuilder();
        foreach (var header in request.Headers)
        {
            if (IsHopByHop(header.Name))
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

    private async Task WorkerAsync()
    {
        while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            if (HoldForTests is { } hold)
            {
                Interlocked.Increment(ref holdEntries);
                await hold.ConfigureAwait(false);
            }

            while (queue.Reader.TryRead(out var work))
            {
                try
                {
                    if (Sender is { } sender)
                    {
                        await sender(work).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendAsync(work).ConfigureAwait(false);
                    }

                    Interlocked.Increment(ref sent);
                    ProxyMetrics.RecordMirrorSent();
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                    ProxyMetrics.RecordMirrorFailed();
                }
            }
        }
    }

    private static async Task SendAsync(MirrorWork work)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(SendTimeout);
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

    internal readonly record struct MirrorWork(
        string Host, int Port, bool UseHttps, string Method, string Path, string HostHeader, string Headers, byte[]? Body);
}
