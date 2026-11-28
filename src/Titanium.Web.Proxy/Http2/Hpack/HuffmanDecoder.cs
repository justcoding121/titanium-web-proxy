using System;

namespace Titanium.Web.Proxy.Http2.Hpack;

/// <summary>HTTP/2 calls the Huffman decoder that lives in Titanium.Http3.</summary>
internal sealed class HuffmanDecoder
{
    /// <summary>The shared decoder.</summary>
    public static readonly HuffmanDecoder Instance = new();

    /// <summary>Decompresses one Huffman-coded string literal.</summary>
    /// <param name="buf">The compressed bytes.</param>
    /// <returns>The decoded bytes.</returns>
    public ReadOnlyMemory<byte> Decode(byte[] buf) => Titanium.Http3.HuffmanDecoder.Instance.Decode(buf);
}
