using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Http;

internal readonly struct HeaderBlockBudget
{
    internal HeaderBlockBudget(int maxCount, long maxAggregateBytes, PolicyMode mode, bool isRequest)
    {
        MaxCount = maxCount;
        MaxAggregateBytes = maxAggregateBytes;
        Mode = mode;
        IsRequest = isRequest;
    }

    internal int MaxCount { get; }
    internal long MaxAggregateBytes { get; }
    internal PolicyMode Mode { get; }
    internal bool IsRequest { get; }

}

internal static class HeaderParser
{
    internal static ValueTask ReadHeaders(ILineStream reader, HeaderCollection headerCollection,
        CancellationToken cancellationToken, HeaderBlockBudget budget = default)
    {
        // Sync-complete as many header lines as are already buffered (keep-alive leftovers often
        // hold the entire header block), then await only when a fill is required.
        var count = 0;
        long aggregate = 0;
        var observed = false;
        while (reader.DataAvailable)
        {
            var lineVt = reader.ReadLineAsync(cancellationToken);
            if (!lineVt.IsCompletedSuccessfully)
                return ReadHeadersContinueAsync(reader, headerCollection, lineVt, hasPending: true,
                    cancellationToken, budget, count, aggregate, observed);

            var buffered = lineVt.Result;
            if (string.IsNullOrEmpty(buffered)) return default;
            Account(ref count, ref aggregate, ref observed, buffered.Length, budget);
            AddHeaderLine(headerCollection, buffered);
        }

        return ReadHeadersContinueAsync(reader, headerCollection, default, hasPending: false,
            cancellationToken, budget, count, aggregate, observed);
    }

    private static async ValueTask ReadHeadersContinueAsync(ILineStream reader,
        HeaderCollection headerCollection, ValueTask<string?> pendingLine, bool hasPending,
        CancellationToken cancellationToken, HeaderBlockBudget budget, int count, long aggregate, bool observed)
    {
        if (hasPending)
        {
            var pending = await pendingLine;
            if (string.IsNullOrEmpty(pending)) return;
            Account(ref count, ref aggregate, ref observed, pending.Length, budget);
            AddHeaderLine(headerCollection, pending);
        }

        while (true)
        {
            var tmpLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(tmpLine)) break;
            Account(ref count, ref aggregate, ref observed, tmpLine.Length, budget);
            AddHeaderLine(headerCollection, tmpLine);
        }
    }

    /// <summary>
    ///     Reads headers without throwing on cancellation. Returns <see langword="false" /> when cancelled.
    /// </summary>
    internal static ValueTask<bool> TryReadHeadersAsync(HttpStream reader,
        HeaderCollection headerCollection, CancellationToken cancellationToken,
        HeaderBlockBudget budget = default)
    {
        var count = 0;
        long aggregate = 0;
        var observed = false;
        // Prefer byte-span parse while complete lines are already buffered (keep-alive leftover).
        while (reader.TryConsumeHeaderLineFromBuffer(out var emptyLine, out var lineBytes))
        {
            if (emptyLine) return new ValueTask<bool>(true);
            Account(ref count, ref aggregate, ref observed, lineBytes.Length, budget);
            AddHeaderLine(headerCollection, lineBytes);
        }

        // Incomplete line or empty buffer — fall through to the string path (handles multi-fill lines).
        return TryReadHeadersContinueAsync(reader, headerCollection, default, hasPending: false,
            cancellationToken, budget, count, aggregate, observed);
    }

    private static async ValueTask<bool> TryReadHeadersContinueAsync(HttpStream reader,
        HeaderCollection headerCollection,
        ValueTask<(string? Line, bool Cancelled)> pendingLine,
        bool hasPending,
        CancellationToken cancellationToken, HeaderBlockBudget budget, int count, long aggregate, bool observed)
    {
        if (hasPending)
        {
            var (pending, cancelled) = await pendingLine;
            if (cancelled) return false;
            if (string.IsNullOrEmpty(pending)) return true;
            Account(ref count, ref aggregate, ref observed, pending.Length, budget);
            AddHeaderLine(headerCollection, pending);
        }

        while (true)
        {
            // Drain any newly completed lines as bytes before awaiting another string line.
            while (reader.TryConsumeHeaderLineFromBuffer(out var emptyLine, out var lineBytes))
            {
                if (emptyLine) return true;
                Account(ref count, ref aggregate, ref observed, lineBytes.Length, budget);
                AddHeaderLine(headerCollection, lineBytes);
            }

            var (tmpLine, cancelled) = await reader.ReadLineWithResultAsync(cancellationToken);
            if (cancelled) return false;
            if (string.IsNullOrEmpty(tmpLine)) return true;
            Account(ref count, ref aggregate, ref observed, tmpLine.Length, budget);
            AddHeaderLine(headerCollection, tmpLine);
        }
    }

    private static void Account(ref int count, ref long aggregate, ref bool observed, int lineLength,
        in HeaderBlockBudget budget)
    {
        if (budget.Mode == PolicyMode.Disabled) return;
        count++;
        aggregate += lineLength + 2L;
        var countBreach = count > budget.MaxCount;
        var sizeBreach = aggregate > budget.MaxAggregateBytes;
        if (!countBreach && !sizeBreach) return;

        var id = countBreach ? LimitId.HeaderCount : LimitId.HeaderAggregate;
        var observedValue = countBreach ? count : aggregate;
        var limit = countBreach ? budget.MaxCount : budget.MaxAggregateBytes;
        if (budget.Mode == PolicyMode.Observe)
        {
            if (observed) return;
            observed = true;
            ProxyLog.LimitExceeded(ProxyDiagnostics.Logger, id, PolicyMode.Observe, observedValue, limit, "passed");
            return;
        }

        ProxyLog.LimitExceeded(ProxyDiagnostics.Logger, id, PolicyMode.Enforce, observedValue, limit,
            budget.IsRequest ? "431" : "502");
        throw new HeaderLimitsExceededException(countBreach, observedValue, limit, budget.IsRequest);
    }

    private static void AddHeaderLine(HeaderCollection headerCollection, string tmpLine)
    {
        // RFC 9112 §5.2: obs-fold (field-value continuation with leading SP or HTAB) is forbidden.
        // Treated as framing — always enforced, no PolicyMode.
        if (tmpLine.Length > 0 && (tmpLine[0] == ' ' || tmpLine[0] == '\t'))
            throw new FormatException("HTTP/1.x obs-fold continuation is not permitted (RFC 9112 §5.2).");

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

    private static void AddHeaderLine(HeaderCollection headerCollection, ReadOnlySpan<byte> tmpLine)
    {
        // RFC 9112 §5.2: obs-fold (field-value continuation with leading SP or HTAB) is forbidden.
        // Treated as framing — always enforced, no PolicyMode.
        if (tmpLine.Length > 0 && (tmpLine[0] == (byte)' ' || tmpLine[0] == (byte)'\t'))
            throw new FormatException("HTTP/1.x obs-fold continuation is not permitted (RFC 9112 §5.2).");

        var colonIndex = tmpLine.IndexOf((byte)':');
        if (colonIndex == -1) throw new FormatException("Header line should contain a colon character.");

        var nameSpan = TrimAscii(tmpLine.Slice(0, colonIndex));
        var valueSpan = TrimAscii(tmpLine.Slice(colonIndex + 1));

        if (KnownHeaders.TryMatchName(nameSpan, out var knownName))
        {
            if (KnownHeaders.TryMatchValue(valueSpan, out var knownValue))
                headerCollection.AddHeader(knownName, knownValue);
            else
                headerCollection.AddHeader(new HttpHeader(knownName, CopyBytes(valueSpan)));
            return;
        }

        headerCollection.AddHeader(new HttpHeader(CopyBytes(nameSpan), CopyBytes(valueSpan)));
    }

    private static ByteString CopyBytes(ReadOnlySpan<byte> span) => new(span.ToArray());

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] is (byte)' ' or (byte)'\t')
            start++;
        var end = value.Length;
        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
            end--;
        return value.Slice(start, end - start);
    }
}
