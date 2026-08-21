using System;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http;

public class KnownHeader
{
    public string String; // NOSONAR S1104 -- Retained as a field for public binary compatibility.
    internal ByteString String8;

    private KnownHeader(string str)
    {
        String8 = (ByteString)str;
        String = str;
    }

    public override string ToString()
    {
        return String;
    }

    internal bool Equals(ReadOnlySpan<char> value)
    {
        return String.AsSpan().EqualsIgnoreCase(value);
    }

    /// <summary>ASCII case-insensitive match against the interned UTF-8 name/value bytes.</summary>
    internal bool Equals(ReadOnlySpan<byte> value)
    {
        var expected = String8.Span;
        if (expected.Length != value.Length) return false;
        for (var i = 0; i < expected.Length; i++)
        {
            var a = expected[i];
            var b = value[i];
            if (a is >= (byte)'A' and <= (byte)'Z') a = (byte)(a + 32);
            if (b is >= (byte)'A' and <= (byte)'Z') b = (byte)(b + 32);
            if (a != b) return false;
        }

        return true;
    }

    internal bool Equals(string? value)
    {
        return String.EqualsIgnoreCase(value);
    }

    public static implicit operator KnownHeader(string str)
    {
        return new(str);
    }
}