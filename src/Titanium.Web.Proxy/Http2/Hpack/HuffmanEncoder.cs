using System;
using System.IO;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http2.Hpack;

/// <summary>HTTP/2 calls the Huffman encoder that lives in Titanium.Http3.</summary>
internal sealed class HuffmanEncoder
{
    /// <summary>The shared encoder.</summary>
    public static readonly HuffmanEncoder Instance = new();

    /// <summary>Compresses one string literal.</summary>
    /// <param name="output">The output stream for the compressed data.</param>
    /// <param name="data">The string literal to encode.</param>
    public void Encode(BinaryWriter output, ByteString data) =>
        Titanium.Http3.HuffmanEncoder.Instance.Encode(output, data.Span);

    /// <summary>Returns the number of bytes required to Huffman-encode the input.</summary>
    /// <param name="data">The string literal to encode.</param>
    /// <returns>The encoded length in bytes.</returns>
    public int GetEncodedLength(ByteString data) =>
        Titanium.Http3.HuffmanEncoder.Instance.GetEncodedLength(data.Span);
}
