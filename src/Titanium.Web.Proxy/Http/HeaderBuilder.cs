using System;
using System.Buffers;
using System.IO;
using System.Text;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http;

internal class HeaderBuilder
{
    [ThreadStatic]
    private static HeaderBuilder? cached;

    private readonly MemoryStream stream = new(256);

    /// <summary>Rents a thread-local builder (cleared). Caller must <see cref="Return"/> it.</summary>
    public static HeaderBuilder Rent()
    {
        var builder = cached;
        if (builder != null)
        {
            cached = null;
            builder.stream.SetLength(0);
            return builder;
        }

        return new HeaderBuilder();
    }

    /// <summary>Returns a builder to the thread-local cache.</summary>
    public static void Return(HeaderBuilder builder)
    {
        if (cached == null)
            cached = builder;
    }

    public void WriteRequestLine(string httpMethod, string httpUrl, Version version)
    {
        Write(httpMethod);
        Write(" ");
        Write(httpUrl);
        Write(" HTTP/");
        Write(version.Major.ToString());
        Write(".");
        Write(version.Minor.ToString());
        WriteLine();
    }

    public void WriteResponseLine(Version version, int statusCode, string statusDescription)
    {
        Write("HTTP/");
        Write(version.Major.ToString());
        Write(".");
        Write(version.Minor.ToString());
        Write(" ");
        Write(statusCode.ToString());
        Write(" ");
        Write(statusDescription);
        WriteLine();
    }

    public void WriteHeaders(HeaderCollection headers, bool sendProxyAuthorization = true,
        string? upstreamProxyUserName = null, string? upstreamProxyPassword = null)
    {
        if (upstreamProxyUserName != null && upstreamProxyPassword != null)
        {
            WriteHeader(HttpHeader.ProxyConnectionKeepAlive);
            WriteHeader(HttpHeader.GetProxyAuthorizationHeader(upstreamProxyUserName, upstreamProxyPassword));
        }

        foreach (var header in headers)
        {
            if (!sendProxyAuthorization && KnownHeaders.ProxyAuthorization.Equals(header.Name))
                continue;
            WriteHeader(header);
        }

        WriteLine();
    }

    public void WriteHeader(HttpHeader header)
    {
        Write(header.Name);
        Write(": ");
        Write(header.Value);
        WriteLine();
    }

    public void WriteLine()
    {
        var data = ProxyConstants.NewLineBytes;
        stream.Write(data, 0, data.Length);
    }

    public void Write(string str)
    {
        var encoding = HttpHeader.Encoding;

        var buf = ArrayPool<byte>.Shared.Rent(encoding.GetMaxByteCount(str.Length));
        try
        {
            var span = new Span<byte>(buf);
            int bytes = encoding.GetBytes(str.AsSpan(), span);
            stream.Write(span.Slice(0, bytes));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public ArraySegment<byte> GetBuffer()
    {
        if (!stream.TryGetBuffer(out var buffer))
            throw new InvalidOperationException("The header buffer is unexpectedly unavailable.");

        return buffer;
    }

    public string GetString(Encoding encoding)
    {
        var buffer = GetBuffer();
        if (buffer.Array == null)
            throw new InvalidOperationException("The header buffer has no backing array.");

        return encoding.GetString(buffer.Array, buffer.Offset, buffer.Count);
    }
}