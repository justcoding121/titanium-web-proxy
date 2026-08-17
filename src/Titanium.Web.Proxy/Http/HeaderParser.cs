using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Http;

internal static class HeaderParser
{
    internal static ValueTask ReadHeaders(ILineStream reader, HeaderCollection headerCollection,
        CancellationToken cancellationToken)
    {
        // Sync-complete as many header lines as are already buffered (keep-alive leftovers often
        // hold the entire header block), then await only when a fill is required.
        while (reader.DataAvailable)
        {
            var lineVt = reader.ReadLineAsync(cancellationToken);
            if (!lineVt.IsCompletedSuccessfully)
                return ReadHeadersContinueAsync(reader, headerCollection, lineVt, hasPending: true,
                    cancellationToken);

            var buffered = lineVt.Result;
            if (string.IsNullOrEmpty(buffered)) return default;
            AddHeaderLine(headerCollection, buffered);
        }

        return ReadHeadersContinueAsync(reader, headerCollection, default, hasPending: false,
            cancellationToken);
    }

    private static async ValueTask ReadHeadersContinueAsync(ILineStream reader,
        HeaderCollection headerCollection, ValueTask<string?> pendingLine, bool hasPending,
        CancellationToken cancellationToken)
    {
        if (hasPending)
        {
            var pending = await pendingLine;
            if (string.IsNullOrEmpty(pending)) return;
            AddHeaderLine(headerCollection, pending);
        }

        while (true)
        {
            var tmpLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(tmpLine)) break;
            AddHeaderLine(headerCollection, tmpLine);
        }
    }

    /// <summary>
    ///     Reads headers without throwing on cancellation. Returns <see langword="false" /> when cancelled.
    /// </summary>
    internal static ValueTask<bool> TryReadHeadersAsync(HttpStream reader,
        HeaderCollection headerCollection, CancellationToken cancellationToken)
    {
        // Sync-complete as many header lines as are already buffered, then fall through to async
        // only when a socket fill is required.
        while (reader.DataAvailable)
        {
            var lineVt = reader.ReadLineWithResultAsync(cancellationToken);
            if (!lineVt.IsCompletedSuccessfully)
                return TryReadHeadersContinueAsync(reader, headerCollection, lineVt, hasPending: true,
                    cancellationToken);

            var (tmpLine, cancelled) = lineVt.Result;
            if (cancelled) return new ValueTask<bool>(false);
            if (string.IsNullOrEmpty(tmpLine)) return new ValueTask<bool>(true);
            AddHeaderLine(headerCollection, tmpLine);
        }

        return TryReadHeadersContinueAsync(reader, headerCollection, default, hasPending: false,
            cancellationToken);
    }

    private static async ValueTask<bool> TryReadHeadersContinueAsync(HttpStream reader,
        HeaderCollection headerCollection,
        ValueTask<(string? Line, bool Cancelled)> pendingLine,
        bool hasPending,
        CancellationToken cancellationToken)
    {
        if (hasPending)
        {
            var (pending, cancelled) = await pendingLine;
            if (cancelled) return false;
            if (string.IsNullOrEmpty(pending)) return true;
            AddHeaderLine(headerCollection, pending);
        }

        while (true)
        {
            var (tmpLine, cancelled) = await reader.ReadLineWithResultAsync(cancellationToken);
            if (cancelled) return false;
            if (string.IsNullOrEmpty(tmpLine)) return true;
            AddHeaderLine(headerCollection, tmpLine);
        }
    }

    private static void AddHeaderLine(HeaderCollection headerCollection, string tmpLine)
    {
        var colonIndex = tmpLine.IndexOf(':');
        if (colonIndex == -1) throw new FormatException("Header line should contain a colon character.");

        var nameSpan = tmpLine.AsSpan(0, colonIndex);
        var valueSpan = tmpLine.AsSpan(colonIndex + 1).TrimStart();

        if (KnownHeaders.TryMatchName(nameSpan, out var knownName))
        {
            if (KnownHeaders.TryMatchValue(valueSpan, out var knownValue))
                headerCollection.AddHeader(knownName, knownValue);
            else
                headerCollection.AddHeader(new HttpHeader(knownName, valueSpan.Trim().GetByteString()));
            return;
        }

        headerCollection.AddHeader(new HttpHeader(nameSpan.Trim().GetByteString(), valueSpan.Trim().GetByteString()));
    }
}
