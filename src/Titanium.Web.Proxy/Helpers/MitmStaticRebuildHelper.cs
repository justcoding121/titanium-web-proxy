using System;
using System.Collections.Generic;
using System.IO;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Drop-only static HPACK/QPACK rebuild for MITM compressed relay (v2).
///     Re-encodes static-table-only blocks after removing up to four unique headers.
/// </summary>
internal static class MitmStaticRebuildHelper
{
    internal static bool IsStaticOnlyHpackBlock(ReadOnlySpan<byte> block)
    {
        var i = 0;
        while (i < block.Length)
        {
            var b = block[i];
            if ((b & 0x80) != 0)
            {
                if ((b & 0x7f) == 0x7f)
                    return false;
                i++;
                continue;
            }

            if ((b & 0xe0) == 0x20 || (b & 0xc0) == 0x40)
                return false;

            if ((b & 0xf0) is 0x00 or 0x10)
            {
                if (!TrySkipHpackLiteral(block, ref i))
                    return false;
                continue;
            }

            return false;
        }

        return true;
    }

    internal static bool IsStaticOnlyQpackBlock(ReadOnlySpan<byte> block) =>
        block.Length >= 2 && block[0] == 0 && block[1] == 0;

    internal static bool TryRebuildStaticHpackBlock(
        ReadOnlySpan<byte> block,
        MitmCompressedRelayHelper.DroppedNameBuffer dropped,
        out byte[] rebuilt)
    {
        rebuilt = Array.Empty<byte>();
        if (block.IsEmpty || dropped.Count == 0)
            return false;
        if (!IsStaticOnlyHpackBlock(block))
            return false;

        var headers = DecodeStaticHpackBlock(block);
        if (headers == null)
            return false;

        var filtered = new List<(ByteString Name, ByteString Value)>(headers.Count);
        foreach (var (name, value) in headers)
        {
            if (dropped.Contains(name.GetString()))
                continue;
            filtered.Add((name, value));
        }

        if (filtered.Count == headers.Count)
            return false;

        rebuilt = EncodeStaticHpackBlock(filtered);
        return true;
    }

    internal static bool TryRebuildStaticQpackBlock(
        ReadOnlySpan<byte> block,
        MitmCompressedRelayHelper.DroppedNameBuffer dropped,
        out byte[] rebuilt)
    {
        rebuilt = Array.Empty<byte>();
        if (block.IsEmpty || dropped.Count == 0)
            return false;
        if (!IsStaticOnlyQpackBlock(block))
            return false;

        List<(string Name, string Value)> decoded;
        try
        {
            decoded = QpackDecoder.Decode(block);
        }
        catch
        {
            return false;
        }

        var filtered = new List<(string Name, string Value)>(decoded.Count);
        foreach (var (name, value) in decoded)
        {
            if (dropped.Contains(name))
                continue;
            filtered.Add((name, value));
        }

        if (filtered.Count == decoded.Count)
            return false;

        rebuilt = QpackEncoder.Encode(filtered);
        return true;
    }

    internal static bool TryPrepareStaticQpackRelay(
        byte[] capturedBlock,
        MitmCompressedRelayHelper.HeaderRelayBaseline baseline,
        HeaderCollection after,
        out byte[] blockToRelay,
        out MitmCompressedRelayHelper.AddedHeaderBuffer appendLiterals)
    {
        blockToRelay = capturedBlock;
        appendLiterals = default;

        if (baseline.TryDiffAppendOnly(after, MitmCompressedRelayHelper.DefaultMaxAppendHeaders,
                out appendLiterals))
            return true;

        if (baseline.TryDiffDropOnly(after, MitmCompressedRelayHelper.DefaultMaxDrops, out var dropped)
            && TryRebuildStaticQpackBlock(capturedBlock, dropped, out blockToRelay))
            return true;

        return false;
    }

    internal static bool TryPrepareStaticHpackRelay(
        byte[] capturedBlock,
        MitmCompressedRelayHelper.HeaderRelayBaseline baseline,
        HeaderCollection after,
        out byte[] blockToRelay,
        out MitmCompressedRelayHelper.AddedHeaderBuffer appendLiterals)
    {
        blockToRelay = capturedBlock;
        appendLiterals = default;

        if (baseline.TryDiffAppendOnly(after, MitmCompressedRelayHelper.DefaultMaxAppendHeaders,
                out appendLiterals))
            return true;

        if (baseline.TryDiffDropOnly(after, MitmCompressedRelayHelper.DefaultMaxDrops, out var dropped)
            && TryRebuildStaticHpackBlock(capturedBlock, dropped, out blockToRelay))
            return true;

        return false;
    }

    private static List<(ByteString Name, ByteString Value)>? DecodeStaticHpackBlock(ReadOnlySpan<byte> block)
    {
        var headers = new List<(ByteString, ByteString)>();
        var decoder = new Decoder(8192, 0);
        try
        {
            decoder.Decode(block, new HeaderCollector((n, v) => headers.Add((n, v))));
        }
        catch
        {
            return null;
        }

        return headers;
    }

    private static byte[] EncodeStaticHpackBlock(List<(ByteString Name, ByteString Value)> headers)
    {
        var encoder = new Encoder(0);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        foreach (var (name, value) in headers)
            encoder.EncodeHeader(writer, name, value);
        writer.Flush();
        return ms.ToArray();
    }

    private static bool TrySkipHpackLiteral(ReadOnlySpan<byte> block, ref int i)
    {
        if (i >= block.Length)
            return false;
        var b = block[i];
        int nameIndex;
        if ((b & 0xc0) == 0x40)
        {
            nameIndex = b & 0x3f;
            i++;
            if (nameIndex == 0x3f && !TrySkipHpackIntegerContinuation(block, ref i))
                return false;
        }
        else
        {
            nameIndex = b & 0x0f;
            i++;
            if (nameIndex == 0x0f && !TrySkipHpackIntegerContinuation(block, ref i))
                return false;
        }

        if (nameIndex == 0 && !TrySkipHpackString(block, ref i))
            return false;

        return TrySkipHpackString(block, ref i);
    }

    private static bool TrySkipHpackString(ReadOnlySpan<byte> block, ref int i)
    {
        if (i >= block.Length)
            return false;
        var len = block[i] & 0x7f;
        i++;
        if (len == 0x7f)
        {
            if (!TrySkipHpackIntegerContinuation(block, ref i, out var extra))
                return false;
            len = 127 + extra;
        }

        if (i + len > block.Length)
            return false;
        i += len;
        return true;
    }

    private static bool TrySkipHpackIntegerContinuation(ReadOnlySpan<byte> block, ref int i)
        => TrySkipHpackIntegerContinuation(block, ref i, out _);

    private static bool TrySkipHpackIntegerContinuation(ReadOnlySpan<byte> block, ref int i, out int value)
    {
        value = 0;
        var m = 0;
        while (i < block.Length)
        {
            var b = block[i++];
            value += (b & 0x7f) << m;
            if ((b & 0x80) == 0)
                return true;
            m += 7;
            if (m > 28)
                return false;
        }

        return false;
    }

    private sealed class HeaderCollector : IHeaderListener
    {
        private readonly Action<ByteString, ByteString> _add;

        internal HeaderCollector(Action<ByteString, ByteString> add) => _add = add;

        public void AddHeader(ByteString name, ByteString value, bool sensitive) => _add(name, value);
    }
}
