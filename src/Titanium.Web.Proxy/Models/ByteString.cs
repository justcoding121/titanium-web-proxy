using System;
using System.Text;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Models;

internal struct ByteString : IEquatable<ByteString>
{
    internal static readonly ByteString Empty = new(ReadOnlyMemory<byte>.Empty);

    public ReadOnlyMemory<byte> Data { get; }

    public ReadOnlySpan<byte> Span => Data.Span;

    public int Length => Data.Length;

    public ByteString(ReadOnlyMemory<byte> data)
    {
        Data = data;
    }

    public override bool Equals(object? obj)
    {
        return obj is ByteString other && Equals(other);
    }

    public bool Equals(ByteString other)
    {
        return Data.Span.SequenceEqual(other.Data.Span);
    }

    public int IndexOf(byte value)
    {
        return Span.IndexOf(value);
    }

    public ByteString Slice(int start)
    {
        return Data.Slice(start);
    }

    public ByteString Slice(int start, int length)
    {
        return Data.Slice(start, length);
    }

    public override int GetHashCode()
    {
        return Data.GetHashCode();
    }

    public override string ToString()
    {
        return this.GetString();
    }

    public static explicit operator ByteString(string str)
    {
        return new(Encoding.ASCII.GetBytes(str));
    }

    public static implicit operator ByteString(byte[] data)
    {
        return new(data);
    }

    public static implicit operator ByteString(ReadOnlyMemory<byte> data)
    {
        return new(data);
    }

    public byte this[int i] => Span[i];

    /// <summary>ASCII case-insensitive equality (HTTP header tokens).</summary>
    internal bool EqualsIgnoreCaseAscii(ByteString other)
    {
        var a = Span;
        var b = other.Span;
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (ToLowerAscii(a[i]) != ToLowerAscii(b[i])) return false;
        }

        return true;
    }

    /// <summary>ASCII case-insensitive substring search (e.g. Transfer-Encoding: chunked).</summary>
    internal bool SpanContainsIgnoreCaseAscii(ReadOnlySpan<byte> needle)
    {
        var hay = Span;
        if (needle.Length == 0) return true;
        if (needle.Length > hay.Length) return false;
        var last = hay.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (ToLowerAscii(hay[i + j]) != ToLowerAscii(needle[j]))
                {
                    match = false;
                    break;
                }
            }

            if (match) return true;
        }

        return false;
    }

    private static byte ToLowerAscii(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
}