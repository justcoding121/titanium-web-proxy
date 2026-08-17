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
    internal static async ValueTask ReadHeaders(ILineStream reader, HeaderCollection headerCollection,
        CancellationToken cancellationToken)
    {
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
    internal static async ValueTask<bool> TryReadHeadersAsync(HttpStream reader,
        HeaderCollection headerCollection, CancellationToken cancellationToken)
    {
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