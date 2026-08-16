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

    internal override void ResetState()
    {
        base.ResetState();
        StatusCode = 0;
        StatusDescription = string.Empty;
        RequestMethod = string.Empty;
        StreamBodyWriter = null;
    }

    /// <summary>
    ///     Has response body?
    /// </summary>
    public override bool HasBody
    {
        get
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

            // Has body only if response is chunked or content length >0
            // If none are true then check if connection:close header exist, if so write response until server or client terminates the connection
            if (IsChunked || contentLength > 0 || !KeepAlive) return true;

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
            var headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.Connection);

            // HTTP/1.0 is non-persistent by default: the connection is only reusable when the
            // response explicitly opts in with "Connection: keep-alive". Treating a plain HTTP/1.0
            // response as keep-alive would let us pool a connection the server is about to close.
            if (HttpVersion == HttpHeader.Version10)
                return headerValue != null &&
                       headerValue.EqualsIgnoreCase(KnownHeaders.ConnectionKeepAlive.String);

            // HTTP/1.1 (and HTTP/2) are persistent by default unless the response asks to close.
            if (headerValue != null && headerValue.EqualsIgnoreCase(KnownHeaders.ConnectionClose.String))
                return false;

            return true;
        }
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
}