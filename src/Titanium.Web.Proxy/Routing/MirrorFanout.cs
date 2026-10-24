using System;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Routing;

/// <summary>Best-effort mirror enqueue. A full queue increments the drop counter and returns.</summary>
internal static class MirrorFanout
{
    private static int _queued;
    private const int MaxQueued = 32;

    /// <summary>Test sink. When set, the production counter is not used.</summary>
    internal static Action<string, string, string>? Observed { get; set; }

    public static void TryEnqueue(MirrorConfig mirror, Request request)
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

        if (System.Threading.Interlocked.Increment(ref _queued) > MaxQueued)
        {
            System.Threading.Interlocked.Decrement(ref _queued);
            Titanium.Web.Proxy.Diagnostics.ProxyMetrics.RecordMirrorDrop();
            return;
        }

        System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            System.Threading.Interlocked.Decrement(ref _queued);
        }, null);
    }
}
