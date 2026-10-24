using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Best-effort mirror. A full queue increments the drop counter. The copy is a separate connection
/// and never writes the client's response.
/// </summary>
internal static class MirrorFanout
{
    private const int MaxQueued = 32;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);
    private static readonly Channel<MirrorWork> Queue = Channel.CreateBounded<MirrorWork>(new BoundedChannelOptions(MaxQueued)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    private static int _started;
    private static int _queued;
    private static int _dropped;
    private static int _skipped;
    private static int _sent;
    private static int _failed;
    private static int _holdEntries;

    /// <summary>Test sink. When set, nothing is queued.</summary>
    internal static Action<string, string, string>? Observed { get; set; }

    internal static int QueuedForTests => _queued;
    internal static int DroppedForTests => _dropped;
    internal static int SkippedForTests => _skipped;
    internal static int SentForTests => _sent;
    internal static int FailedForTests => _failed;
    internal static int HoldEntriesForTests => _holdEntries;

    /// <summary>When set, the worker waits before opening a socket. Tests use it to fill the queue.</summary>
    internal static Task? HoldForTests { get; set; }

    internal static void ResetForTests()
    {
        Observed = null;
        HoldForTests = null;

        // Work queued by an earlier test would otherwise delay the next one behind its connect timeouts.
        while (Queue.Reader.TryRead(out _))
        {
        }

        _queued = 0;
        _dropped = 0;
        _skipped = 0;
        _sent = 0;
        _failed = 0;
        _holdEntries = 0;
    }

    public static void TryEnqueue(MirrorConfig mirror, Request request, DestinationConfig? destination = null)
    {
        if (mirror.SamplePercent <= 0)
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

        if (destination is null || !TryCopyBody(mirror, request, out var body))
        {
            Interlocked.Increment(ref _skipped);
            ProxyMetrics.RecordMirrorSkipped();
            return;
        }

        var port = destination.Port == 0 ? (destination.UseHttps ? 443 : 80) : destination.Port;
        var work = new MirrorWork(
            destination.Address,
            port,
            destination.UseHttps,
            request.Method ?? "GET",
            path,
            request.Host ?? destination.Address,
            CopyHeaders(request),
            body);

        if (!Queue.Writer.TryWrite(work))
        {
            Interlocked.Increment(ref _dropped);
            ProxyMetrics.RecordMirrorDrop();
            return;
        }

        Interlocked.Increment(ref _queued);
        ProxyMetrics.RecordMirrorQueued();
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            _ = Task.Run(WorkerAsync);
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

    private static async Task WorkerAsync()
    {
        while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            if (HoldForTests is { } hold)
            {
                Interlocked.Increment(ref _holdEntries);
                await hold.ConfigureAwait(false);
            }

            while (Queue.Reader.TryRead(out var work))
            {
                try
                {
                    await SendAsync(work).ConfigureAwait(false);
                    Interlocked.Increment(ref _sent);
                    ProxyMetrics.RecordMirrorSent();
                }
                catch
                {
                    Interlocked.Increment(ref _failed);
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
            // Same rule as any origin leg: the certificate is validated against the destination name.
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
            // Discard the mirror response.
        }
    }

    private readonly record struct MirrorWork(
        string Host, int Port, bool UseHttps, string Method, string Path, string HostHeader, string Headers, byte[]? Body);
}
