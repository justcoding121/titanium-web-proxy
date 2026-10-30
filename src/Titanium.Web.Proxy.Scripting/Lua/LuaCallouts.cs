using System.Globalization;
using System.Net;
using System.Text;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>
/// <c>http.call</c> for Lua plugins. The URL's host must match <c>calloutHosts</c> (<c>host</c>, <c>host:port</c>, or
/// <c>*.domain</c>); with none configured every callout is refused. Callouts never use a system proxy, never follow
/// redirects, never send cookies, and read at most <c>limits.bufferedBodyBytes</c> of the answer.
/// </summary>
internal sealed class LuaCallouts : IDisposable
{
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "transfer-encoding", "connection", "upgrade", "te", "trailer", "keep-alive",
        "proxy-connection", "proxy-authorization",
    };

    private readonly HttpMessageInvoker _client;
    private readonly bool _ownsClient;
    private readonly IReadOnlyList<string> _hosts;
    private readonly int _concurrency;
    private readonly long _maxBody;
    private int _inFlight;

    public LuaCallouts(LuaOptions options, HttpMessageHandler? handler = null)
    {
        _hosts = options.CalloutHosts;
        _concurrency = options.CalloutConcurrency;
        _maxBody = options.BufferedBodyBytes;
        _ownsClient = true;
        _client = new HttpMessageInvoker(handler ?? new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        }, disposeHandler: true);
    }

    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Runs one callout and returns the encoded resume values: <c>{status, headers, body}</c> or <c>nil, err</c>.
    /// Throws <see cref="OperationCanceledException"/> only when <paramref name="token"/> is cancelled.
    /// </summary>
    public async ValueTask<byte[]> CallAsync(LuaTable op, CancellationToken token) // NOSONAR S3776 -- Lua callout path validates URL, headers, and response limits together.
    {
        var url = op.Str("url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
        {
            return LuaRuntime.Values(null, "http.call: url must be an absolute http or https URL without credentials");
        }

        if (!_hosts.Any(h => Allows(h, uri)))
        {
            return LuaRuntime.Values(null, $"http.call: {uri.Authority} is not in calloutHosts");
        }

        if (Interlocked.Increment(ref _inFlight) > _concurrency)
        {
            Interlocked.Decrement(ref _inFlight);
            return LuaRuntime.Values(null, $"http.call: {_concurrency} callouts already in flight");
        }

        var timeoutMs = (int)Math.Clamp(op.Int("timeout_ms") ?? LuaOptions.DefaultCalloutTimeoutMs, 1, 60_000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutMs);
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(op.Str("method") ?? "GET"), uri);
            if (op.Bytes("body") is { } body)
            {
                request.Content = new ByteArrayContent(body);
            }

            if (op.Table("headers") is { } headers)
            {
                foreach (var pair in headers.Fields)
                {
                    if (HopHeaders.Contains(pair.Key) || LuaReader.AsString(pair.Value) is not { } value ||
                        value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                    {
                        continue;
                    }

                    if (!request.Headers.TryAddWithoutValidation(pair.Key, value))
                    {
                        request.Content ??= new ByteArrayContent([]);
                        request.Content.Headers.TryAddWithoutValidation(pair.Key, value);
                    }
                }
            }

            using var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var bytes = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return LuaRuntime.Values(null, $"http.call: response body is larger than {_maxBody} bytes");
            }

            var result = new LuaTable();
            result.Fields["status"] = (long)response.StatusCode;
            result.Fields["headers"] = ResponseHeaders(response);
            result.Fields["body"] = bytes;
            return LuaRuntime.Values(result);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return LuaRuntime.Values(null, $"http.call: timed out after {timeoutMs} ms");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or FormatException)
        {
            return LuaRuntime.Values(null, "http.call: " + ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    internal static bool Allows(string allowed, Uri uri)
    {
        allowed = allowed.Trim();
        if (allowed.StartsWith("*.", StringComparison.Ordinal))
        {
            return uri.Host.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase) && uri.Host.Length > allowed.Length - 1;
        }

        return string.Equals(allowed, uri.Host, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(allowed, uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<byte[]?> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > _maxBody)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _maxBody)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static LuaTable ResponseHeaders(HttpResponseMessage response)
    {
        var table = new LuaTable();
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            var name = header.Key.ToLowerInvariant();
            var value = string.Join(", ", header.Value);
            table.Fields[name] = table.Fields.TryGetValue(name, out var existing) && existing is byte[] old
                ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(old) + ", " + value)
                : Encoding.UTF8.GetBytes(value);
        }

        return table;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
