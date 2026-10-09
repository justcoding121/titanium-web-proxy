using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Http(s) response object
/// </summary>
[TypeConverter(typeof(ExpandableObjectConverter))]
public class Response : RequestResponseBase
{
    /// <summary>
    ///     Constructor.
    /// </summary>
    public Response()
    {
    }

    /// <summary>
    ///     Constructor.
    /// </summary>
    public Response(byte[] body)
    {
        Body = body;
    }

    /// <summary>
    ///     Response Status Code.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    ///     Response Status description.
    /// </summary>
    public string StatusDescription { get; set; } = string.Empty;

    internal string RequestMethod { get; set; } = string.Empty;

    /// <summary>
    ///     When set via SessionEventArgs.RespondStreaming, this delegate is invoked to produce the response
    ///     body as a live stream (without buffering it in memory). The provided stream frames writes as HTTP/1.1
    ///     chunks when the response is chunked, or writes raw bytes when a Content-Length is set.
    /// </summary>
    internal Func<Stream, CancellationToken, Task>? StreamBodyWriter { get; set; }

    /// <summary>
    ///     HTTP/3 origin responses only: drains the still-unread QUIC response body (wire bytes, no
    ///     <c>OnResponseBodyWrite</c> hook) into the destination and releases the origin stream. It lets
    ///     <c>SessionEventArgs.GetResponseBody</c> buffer a body that otherwise lives on the QUIC stream
    ///     (there is no <c>HttpClient.Connection</c> to read from, unlike HTTP/1.x). Null once consumed
    ///     or when the body is not backed by a live QUIC stream.
    /// </summary>
    internal Func<Stream, CancellationToken, Task>? Http3RawBodyDrain { get; set; }

    /// <summary>
    ///     Clears wire state so this instance can carry the next keep-alive response.
    ///     Must zero <see cref="StatusCode"/> and drop <see cref="StreamBodyWriter"/> —
    ///     <c>ReceiveResponse</c> no-ops when StatusCode != 0, and a leftover writer hangs the next GET.
    /// </summary>
    internal void ResetForKeepAlive()
    {
        ResetWireState();
        StatusCode = 0;
        StatusDescription = string.Empty;
        RequestMethod = string.Empty;
        StreamBodyWriter = null;
        Http3RawBodyDrain = null;
    }

    /// <summary>
    ///     Has response body?
    /// </summary>
    public override bool HasBody
    {
        get // NOSONAR S3776 -- Body-framing rules share status/method state; splitting the accessor adds disproportionate regression risk.
        {
            // RFC 9110 section 6.4.1: a 1xx, 204 or 304 response never has a body, regardless of
            // any Content-Length/Transfer-Encoding header the server sent - those headers describe
            // the representation the resource *would* carry, not bytes actually on this wire (a
            // 304's Content-Length, for instance, must not be used for framing). A response to
            // HEAD never has a body for the same reason, and a successful (2xx) response to
            // CONNECT never has one either: once tunneling begins there is no further HTTP framing
            // on the connection. These status/method exclusions must run before any framing check
            // below, since "!KeepAlive" would otherwise short-circuit a 204/304 "Connection: close"
            // response to "has body".
            if (StatusCode is >= 100 and < 200) return false;
            if (StatusCode == 204 || StatusCode == 304) return false;
            if (RequestMethod == "HEAD") return false;
            if (RequestMethod == "CONNECT" && StatusCode is >= 200 and < 300) return false;

            var contentLength = ContentLength;

            // If content length is set to 0 the response has no body
            if (contentLength == 0) return false;

            // Positive CL is the common keep-alive tiny-GET case — check it before IsChunked /
            // KeepAlive so we do not pay two more header lookups per response.
            if (contentLength > 0) return true;

            // Chunked, or connection:close with no length (read until peer closes).
            if (IsChunked || !KeepAlive) return true;

            // HTTP/2 and HTTP/3 may omit Content-Length; body length is framed by DATA/END_STREAM
            // (or QUIC stream fin), not by Content-Length / Transfer-Encoding.
            if (ContentLength == -1 && HttpVersion.Major >= 2) return true;

            // has response if connection:keep-alive header exist and when version is http/1.0
            // Because in Http 1.0 server can return a response without content-length (expectation being client would read until end of stream)
            if (KeepAlive && HttpVersion == HttpHeader.Version10) return true;

            return false;
        }
    }

    /// <summary>
    ///     Keep the connection alive?
    /// </summary>
    public bool KeepAlive
    {
        get
        {
            // Connection is a comma-separated token list (RFC 9110 §7.6.1). "close" wins;
            // duplicates / "close, te" must not keep the socket in the pool.
            var close = false;
            var keepAlive = false;
            if (Headers.Headers.TryGetValue(KnownHeaders.Connection.String, out var unique))
                ClassifyConnectionTokens(unique.ValueData.Span, ref close, ref keepAlive);
            else if (Headers.NonUniqueHeaders.TryGetValue(KnownHeaders.Connection.String, out var list))
            {
                foreach (var header in list)
                    ClassifyConnectionTokens(header.ValueData.Span, ref close, ref keepAlive);
            }

            if (HttpVersion == HttpHeader.Version10)
                return keepAlive && !close;

            return !close;
        }
    }

    private static void ClassifyConnectionTokens(ReadOnlySpan<byte> value, ref bool close, ref bool keepAlive)
    {
        while (!value.IsEmpty)
        {
            var comma = value.IndexOf((byte)',');
            var token = comma >= 0 ? value[..comma] : value;
            value = comma >= 0 ? value[(comma + 1)..] : default;
            token = TrimAsciiWs(token);
            if (AsciiEqualsIgnoreCase(token, "close"u8))
                close = true;
            else if (AsciiEqualsIgnoreCase(token, "keep-alive"u8))
                keepAlive = true;
        }
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x is >= (byte)'A' and <= (byte)'Z') x = (byte)(x + 32);
            if (y is >= (byte)'A' and <= (byte)'Z') y = (byte)(y + 32);
            if (x != y) return false;
        }

        return true;
    }

    private static ReadOnlySpan<byte> TrimAsciiWs(ReadOnlySpan<byte> span)
    {
        while (!span.IsEmpty && span[0] is (byte)' ' or (byte)'\t')
            span = span[1..];
        while (!span.IsEmpty && span[^1] is (byte)' ' or (byte)'\t')
            span = span[..^1];
        return span;
    }

    /// <summary>
    ///     Gets the header text.
    /// </summary>
    public override string HeaderText
    {
        get
        {
            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteResponseLine(HttpVersion, StatusCode, StatusDescription);
            headerBuilder.WriteHeaders(Headers);
            return headerBuilder.GetString(HttpHeader.Encoding);
        }
    }

    internal override void EnsureBodyAvailable(bool throwWhenNotReadYet = true)
    {
        if (BodyInternal != null) return;

        if (!HasBody) throw new BodyNotFoundException("Response don't have a body.");

        if (!IsBodyRead && throwWhenNotReadYet)
            throw new InvalidOperationException("Response body is not read yet. " +
                                "Use SessionEventArgs.GetResponseBody() or SessionEventArgs.GetResponseBodyAsString() " +
                                "method to read the response body.");
    }

    internal static void ParseResponseLine(string httpStatus, out Version version, out int statusCode,
        out string statusDescription)
    {
        var firstSpace = httpStatus.IndexOf(' ');
        if (firstSpace == -1) throw new FormatException("Invalid HTTP status line: " + httpStatus);

        var httpVersion = httpStatus.AsSpan(0, firstSpace);

        version = HttpHeader.Version11;
        if (httpVersion.EqualsIgnoreCase("HTTP/1.0".AsSpan())) version = HttpHeader.Version10;

        var secondSpace = httpStatus.IndexOf(' ', firstSpace + 1);
        if (secondSpace != -1)
        {
            statusCode = int.Parse(httpStatus.AsSpan(firstSpace + 1, secondSpace - firstSpace - 1));
            var description = httpStatus.AsSpan(secondSpace + 1);
            statusDescription = description.Equals("OK", StringComparison.Ordinal) ? "OK" : description.ToString();
        }
        else
        {
            statusCode = int.Parse(httpStatus.AsSpan(firstSpace + 1));
            statusDescription = string.Empty;
        }
    }

    /// <summary>
    ///     Parse a status line from UTF-8/ASCII bytes without allocating the full line string.
    /// </summary>
    internal static void ParseResponseLine(ReadOnlySpan<byte> httpStatus, out Version version, out int statusCode,
        out string statusDescription)
    {
        var firstSpace = httpStatus.IndexOf((byte)' ');
        if (firstSpace == -1)
            throw new FormatException("Invalid HTTP status line.");

        version = HttpHeader.Version11;
        if (IsHttp10(httpStatus.Slice(0, firstSpace)))
            version = HttpHeader.Version10;

        var rest = httpStatus.Slice(firstSpace + 1);
        var secondSpace = rest.IndexOf((byte)' ');
        if (secondSpace != -1)
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(rest.Slice(0, secondSpace), out statusCode, out _))
                throw new FormatException("Invalid HTTP status line.");

            var description = rest.Slice(secondSpace + 1);
            statusDescription = description.SequenceEqual("OK"u8)
                ? "OK"
                : System.Text.Encoding.ASCII.GetString(description);
        }
        else
        {
            if (!System.Buffers.Text.Utf8Parser.TryParse(rest, out statusCode, out _))
                throw new FormatException("Invalid HTTP status line.");

            statusDescription = string.Empty;
        }
    }

    private static bool IsHttp10(ReadOnlySpan<byte> httpVersion)
    {
        if (httpVersion.Length != 8) return false;
        ReadOnlySpan<byte> expected = "HTTP/1.0"u8;
        for (var i = 0; i < 8; i++)
        {
            var c = httpVersion[i];
            if (c is >= (byte)'a' and <= (byte)'z') c = (byte)(c - 32);
            if (c != expected[i]) return false;
        }

        return true;
    }
}