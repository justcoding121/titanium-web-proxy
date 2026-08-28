using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http.Responses;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Factory methods that build synthetic <see cref="Response" /> or <see cref="StreamingProxyResult" />
    ///     objects for use with <see cref="EventArguments.SessionEventArgs.Respond(Response, bool)" /> and
    ///     <see cref="EventArguments.SessionEventArgs.RespondStreaming(StreamingProxyResult, bool)" />.
/// </summary>
public static class ProxyResults
{
    private const string HtmlContentType = "text/html; charset=utf-8";
    private const string TextContentType = "text/plain; charset=utf-8";
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>
    ///     Builds a buffered HTML response.
    /// </summary>
    /// <param name="content">HTML body.</param>
    /// <param name="status">HTTP status code.</param>
    /// <returns>A response ready to pass to <see cref="EventArguments.SessionEventArgs.Respond(Response, bool)" />.</returns>
    /// <remarks>
    ///     Sets <c>Content-Type: text/html; charset=utf-8</c>. For API clients that expect JSON, use
    ///     <see cref="Json{T}" /> instead.
    /// </remarks>
    public static Response Html(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = CreateBufferedResponse(status);
        response.ContentType = HtmlContentType;
        response.Body = response.Encoding.GetBytes(content ?? string.Empty);
        return response;
    }

    /// <summary>
    ///     Builds a buffered plain-text response.
    /// </summary>
    /// <param name="content">Text body.</param>
    /// <param name="status">HTTP status code.</param>
    /// <returns>A response ready to pass to <see cref="EventArguments.SessionEventArgs.Respond(Response, bool)" />.</returns>
    /// <remarks>
    ///     Sets <c>Content-Type: text/plain; charset=utf-8</c>. For Newtonsoft.Json callers, serialize
    ///     manually and pass the JSON string here.
    /// </remarks>
    public static Response Text(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = CreateBufferedResponse(status);
        response.ContentType = TextContentType;
        response.Body = response.Encoding.GetBytes(content ?? string.Empty);
        return response;
    }

    /// <summary>
    ///     Builds a buffered response from raw bytes.
    /// </summary>
    /// <param name="data">Body bytes.</param>
    /// <param name="contentType">Full Content-Type value (for example <c>image/png</c>).</param>
    /// <param name="status">HTTP status code.</param>
    /// <returns>A response ready to pass to <see cref="EventArguments.SessionEventArgs.Respond(Response, bool)" />.</returns>
    public static Response Bytes(byte[] data, string contentType, HttpStatusCode status = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        var response = CreateBufferedResponse(status);
        response.ContentType = contentType;
        response.Body = data;
        return response;
    }

    /// <summary>
    ///     Builds a buffered JSON response.
    /// </summary>
    /// <typeparam name="T">Type to serialize.</typeparam>
    /// <param name="value">Value to serialize.</param>
    /// <param name="status">HTTP status code.</param>
    /// <param name="typeInfo">Optional source-generated type info for NativeAOT scenarios.</param>
    /// <returns>A response ready to pass to <see cref="EventArguments.SessionEventArgs.Respond(Response, bool)" />.</returns>
    /// <remarks>
    ///     Uses <see cref="System.Text.Json.JsonSerializer" />. Pass a <see cref="JsonTypeInfo{T}" /> for
    ///     NativeAOT / source-gen scenarios. For Newtonsoft.Json callers, serialize manually and use
    ///     <see cref="Text" />.
    /// </remarks>
    public static Response Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK, JsonTypeInfo<T>? typeInfo = null)
    {
        var body = typeInfo != null
            ? JsonSerializer.SerializeToUtf8Bytes(value, typeInfo)
            : JsonSerializer.SerializeToUtf8Bytes(value);

        return Bytes(body, JsonContentType, status);
    }

    /// <summary>
    ///     Builds a buffered response with any status and a plain-text body.
    /// </summary>
    /// <param name="status">HTTP status code.</param>
    /// <param name="body">Plain-text body.</param>
    /// <returns>A response ready to pass to <see cref="EventArguments.SessionEventArgs.Respond(Response, bool)" />.</returns>
    public static Response WithStatus(HttpStatusCode status, string body = "")
    {
        return Text(body, status);
    }

    /// <summary>
    ///     Builds a 204 No Content response.
    /// </summary>
    /// <returns>A response ready to pass to <see cref="EventArguments.SessionEventArgs.Respond(Response, bool)" />.</returns>
    public static Response NoContent()
    {
        return new GenericResponse(HttpStatusCode.NoContent);
    }

    /// <summary>
    ///     Builds a redirect response with a <c>Location</c> header.
    /// </summary>
    /// <param name="url">Redirect target URL.</param>
    /// <param name="status">Redirect status (default 302 Found).</param>
    /// <returns>A response ready to pass to <see cref="EventArguments.SessionEventArgs.Respond(Response, bool)" />.</returns>
    /// <remarks>
    ///     Default status is 302 Found. Use <see cref="HttpStatusCode.MovedPermanently" /> (301) for
    ///     canonical URL changes, or <see cref="HttpStatusCode.TemporaryRedirect" /> (307) /
    ///     <see cref="HttpStatusCode.PermanentRedirect" /> (308) to preserve the request method.
    /// </remarks>
    public static Response Redirect(string url, HttpStatusCode status = HttpStatusCode.Found)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);

        var response = new GenericResponse(status);
        response.Headers.AddHeader(KnownHeaders.Location, url);
        response.Body = Array.Empty<byte>();
        return response;
    }

    /// <summary>
    ///     Builds a streamed synthetic response with a caller-supplied body writer.
    /// </summary>
    /// <param name="status">HTTP status code.</param>
    /// <param name="contentType">Full Content-Type value.</param>
    /// <param name="writeBody">Delegate that writes the body to the provided stream.</param>
    /// <param name="contentLength">
    ///     When set, the body is written with fixed length framing; otherwise chunked / DATA framing is used.
    /// </param>
    /// <returns>A streaming result for <see cref="EventArguments.SessionEventArgs.RespondStreaming(StreamingProxyResult, bool)" />.</returns>
    public static StreamingProxyResult Stream(
        HttpStatusCode status,
        string contentType,
        Func<Stream, CancellationToken, Task> writeBody,
        long? contentLength = null)
    {
        ArgumentNullException.ThrowIfNull(writeBody);
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        var response = new GenericResponse(status);
        response.ContentType = contentType;
        if (contentLength is >= 0) response.ContentLength = contentLength.Value;

        return new StreamingProxyResult(response, writeBody);
    }

    /// <summary>
    ///     Builds a streamed response that serves a file from disk without buffering it in memory.
    /// </summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="contentType">Full Content-Type value.</param>
    /// <param name="status">HTTP status code.</param>
    /// <returns>A streaming result for <see cref="EventArguments.SessionEventArgs.RespondStreaming(StreamingProxyResult, bool)" />.</returns>
    /// <remarks>
    ///     Does not handle HTTP <c>Range</c> requests. Clients expecting 206 Partial Content (browser
    ///     <c>&lt;video&gt;</c>, download managers with resume) must use <see cref="Stream" /> with custom
    ///     range logic instead.
    /// </remarks>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static StreamingProxyResult File(string path, string contentType, HttpStatusCode status = HttpStatusCode.OK)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists) throw new FileNotFoundException("File not found.", path);

        return Stream(status, contentType, async (stream, ct) =>
        {
            await using var fileStream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
            await fileStream.CopyToAsync(stream, ct).ConfigureAwait(false);
        }, fileInfo.Length);
    }

    private static Response CreateBufferedResponse(HttpStatusCode status)
    {
        return status == HttpStatusCode.OK ? new OkResponse() : new GenericResponse(status);
    }
}
