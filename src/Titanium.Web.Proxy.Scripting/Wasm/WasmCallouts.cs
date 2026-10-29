using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>The response of a callout while its callback runs: what the callout header maps, buffers and get_status read.</summary>
internal sealed class WasmCalloutResult
{
    public WasmHeaderMap? Headers { get; init; }

    public WasmHeaderMap? Trailers { get; init; }

    public byte[] Body { get; init; } = [];

    public int GrpcStatus { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// HTTP and gRPC callouts for one pool (<c>proxy_http_call</c>, <c>proxy_grpc_call</c>, <c>proxy_grpc_stream</c>). A
/// callout runs on <see cref="HttpClient"/> with no VM lock held; its response is delivered by taking the VM's gate and
/// calling the guest, so a request paused on a callout resumes when the guest continues it from that callback.
/// </summary>
/// <remarks>
/// The target is named by the <c>upstream</c> (or gRPC <c>service</c>) argument as <c>host</c>, <c>host:port</c> or an
/// <c>http://</c> / <c>https://</c> origin, and must match the plugin's <c>calloutHosts</c> (<c>host</c>,
/// <c>host:port</c>, or <c>*.domain</c>). Callouts never go through a system proxy and never follow redirects.
/// </remarks>
internal sealed class WasmCallouts : IDisposable
{
    private const string GrpcContentType = "application/grpc";

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        EnableMultipleHttp2Connections = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade", "te", "host", "content-length",
    };

    private readonly WasmVmPool _pool;
    private readonly ConcurrentDictionary<int, GrpcStreamState> _streams = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _nextToken;
    private int _inFlight;

    public WasmCallouts(WasmVmPool pool) => _pool = pool;

    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary><c>proxy_http_call</c>. Starts the request and returns its token; the answer arrives in <c>proxy_on_http_call_response</c>.</summary>
    public int HttpCall(WasmVm vm, string upstream, List<KeyValuePair<string, string>> headers, byte[] body, int timeoutMs, out int token)
    {
        token = 0;
        var scheme = Find(headers, ":scheme");
        if (!TryResolve(vm, upstream, scheme ?? "http", out var origin))
        {
            return WasmResult.BadArgument;
        }

        var path = Find(headers, ":path") ?? "/";
        if (!path.StartsWith('/'))
        {
            return WasmResult.BadArgument;
        }

        var request = new HttpRequestMessage(new HttpMethod(Find(headers, ":method") ?? (body.Length > 0 ? "POST" : "GET")), new Uri(origin, path));
        if (Find(headers, ":authority") is { Length: > 0 } authority)
        {
            request.Headers.Host = authority;
        }

        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
        }

        CopyHeaders(headers, request);
        if (!TryBegin(vm, out token, out var contextId))
        {
            request.Dispose();
            return WasmResult.InternalFailure;
        }

        var callToken = token;
        _ = Task.Run(() => RunHttpCallAsync(vm, contextId, callToken, request, timeoutMs));
        return WasmResult.Ok;
    }

    /// <summary><c>proxy_grpc_call</c>: one unary call. Success arrives in <c>proxy_on_grpc_receive</c>, failure in <c>proxy_on_grpc_close</c>.</summary>
    public int GrpcCall(WasmVm vm, string service, string serviceName, string method, List<KeyValuePair<string, string>> metadata,
        byte[] message, int timeoutMs, out int token)
    {
        token = 0;
        if (!TryResolve(vm, service, "http", out var origin) || serviceName.Length == 0 || method.Length == 0)
        {
            return WasmResult.BadArgument;
        }

        var request = GrpcRequest(origin, serviceName, method, metadata, timeoutMs);
        request.Content = new ByteArrayContent(Frame(message));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(GrpcContentType);
        if (!TryBegin(vm, out token, out var contextId))
        {
            request.Dispose();
            return WasmResult.InternalFailure;
        }

        var callToken = token;
        _ = Task.Run(() => RunGrpcCallAsync(vm, contextId, callToken, request, timeoutMs));
        return WasmResult.Ok;
    }

    /// <summary><c>proxy_grpc_stream</c>: opens a stream the guest writes with <c>proxy_grpc_send</c>.</summary>
    public int GrpcStream(WasmVm vm, string service, string serviceName, string method, List<KeyValuePair<string, string>> metadata, out int token)
    {
        token = 0;
        if (!TryResolve(vm, service, "http", out var origin) || serviceName.Length == 0 || method.Length == 0)
        {
            return WasmResult.BadArgument;
        }

        var request = GrpcRequest(origin, serviceName, method, metadata, 0);
        var state = new GrpcStreamState(_shutdown.Token, _pool.Limits.BufferedBodyBytes);
        request.Content = new GrpcStreamContent(state);
        if (!TryBegin(vm, out token, out var contextId))
        {
            request.Dispose();
            return WasmResult.InternalFailure;
        }

        _streams[token] = state;
        var callToken = token;
        _ = Task.Run(() => RunGrpcStreamAsync(vm, contextId, callToken, request, state));
        return WasmResult.Ok;
    }

    /// <summary><c>proxy_grpc_send</c>: writes one message; <paramref name="endStream"/> half-closes the stream.</summary>
    public int GrpcSend(int token, byte[] message, bool endStream)
    {
        if (!_streams.TryGetValue(token, out var state))
        {
            return WasmResult.NotFound;
        }

        if (!state.TryWrite(message))
        {
            return WasmResult.BadArgument;
        }

        if (endStream)
        {
            state.Outgoing.Writer.TryComplete();
        }

        return WasmResult.Ok;
    }

    /// <summary><c>proxy_grpc_close</c>: half-closes the stream; the server's answer is still delivered.</summary>
    public int GrpcClose(int token)
    {
        if (!_streams.TryGetValue(token, out var state))
        {
            return WasmResult.NotFound;
        }

        state.Outgoing.Writer.TryComplete();
        return WasmResult.Ok;
    }

    /// <summary><c>proxy_grpc_cancel</c>: aborts the stream; no further callback is delivered for it.</summary>
    public int GrpcCancel(int token)
    {
        if (!_streams.TryRemove(token, out var state))
        {
            return WasmResult.NotFound;
        }

        state.Cancel();
        return WasmResult.Ok;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var state in _streams.Values)
        {
            state.Cancel();
        }

        _streams.Clear();
    }

    private bool TryResolve(WasmVm vm, string target, string defaultScheme, out Uri origin)
    {
        origin = null!;
        var hosts = vm.Effective?.Plugin.CalloutHosts;
        var text = target.Contains("://", StringComparison.Ordinal) ? target : defaultScheme + "://" + target;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.PathAndQuery is not ("/" or "") || uri.Fragment.Length > 0)
        {
            _pool.Log(ProxyLogLevel.Warn, $"callout target '{target}' is not a host, host:port or http(s) origin");
            return false;
        }

        if (hosts is null || !hosts.Any(h => Allows(h, uri)))
        {
            _pool.Log(ProxyLogLevel.Warn, $"callout to '{uri.Authority}' is not in calloutHosts");
            return false;
        }

        origin = new Uri(uri.GetLeftPart(UriPartial.Authority));
        return true;
    }

    private static bool Allows(string allowed, Uri uri)
    {
        allowed = allowed.Trim();
        if (allowed.StartsWith("*.", StringComparison.Ordinal))
        {
            return uri.Host.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase) && uri.Host.Length > allowed.Length - 1;
        }

        return string.Equals(allowed, uri.Host, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(allowed, uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Counts the callout against the concurrency limit, issues its token, and holds a paused request open for it.</summary>
    private bool TryBegin(WasmVm vm, out int token, out int contextId)
    {
        token = 0;
        contextId = vm.Effective?.Id ?? 0;
        if (vm.Effective is null)
        {
            return false;
        }

        if (Interlocked.Increment(ref _inFlight) > _pool.Limits.CalloutConcurrency)
        {
            Interlocked.Decrement(ref _inFlight);
            _pool.Log(ProxyLogLevel.Warn, $"callout refused: {_pool.Limits.CalloutConcurrency} already in flight");
            return false;
        }

        token = Interlocked.Increment(ref _nextToken) & int.MaxValue;
        if (vm.Effective is WasmHttpContext http)
        {
            http.PendingWork++;
        }

        return true;
    }

    /// <summary>Delivers one callback for a callout. <paramref name="last"/> releases the callout's hold on a paused request.</summary>
    private void Deliver(WasmVm vm, int contextId, WasmCalloutResult? result, bool last, string export, Func<int[]> args)
    {
        _pool.Deliver(vm, contextId, context =>
        {
            vm.Callout = result;
            try
            {
                vm.Call(export, args());
            }
            finally
            {
                vm.Callout = null;
                if (last && context is WasmHttpContext http)
                {
                    http.PendingWork = Math.Max(0, http.PendingWork - 1);
                }
            }
        });
    }

    private async Task RunHttpCallAsync(WasmVm vm, int contextId, int token, HttpRequestMessage request, int timeoutMs)
    {
        WasmCalloutResult? result = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(CalloutTimeout(timeoutMs));
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var body = await ReadLimitedAsync(response.Content, _pool.Limits.BufferedBodyBytes, timeout.Token).ConfigureAwait(false);
            var headers = new List<KeyValuePair<string, string>>
            {
                new(":status", ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)),
            };
            AddPairs(headers, response.Headers);
            AddPairs(headers, response.Content.Headers);
            var trailers = new List<KeyValuePair<string, string>>();
            AddPairs(trailers, response.TrailingHeaders);
            result = new WasmCalloutResult { Headers = new WasmHeaderMap(headers), Body = body, Trailers = new WasmHeaderMap(trailers) };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
        {
            // Like Envoy: a failed callout is answered with no headers, no body and no trailers.
            _pool.Log(ProxyLogLevel.Debug, $"http_call {token} to {request.RequestUri} failed: {ex.Message}");
        }
        finally
        {
            request.Dispose();
            Interlocked.Decrement(ref _inFlight);
        }

        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        Deliver(vm, contextId, result, last: true, "proxy_on_http_call_response",
            () => [contextId, token, result?.Headers?.Count ?? 0, result?.Body.Length ?? 0, result?.Trailers?.Count ?? 0]);
    }

    private async Task RunGrpcCallAsync(WasmVm vm, int contextId, int token, HttpRequestMessage request, int timeoutMs)
    {
        WasmCalloutResult result;
        byte[]? message = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(CalloutTimeout(timeoutMs));
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            message = await ReadFrameAsync(stream, _pool.Limits.BufferedBodyBytes, timeout.Token).ConfigureAwait(false);
            while (await ReadFrameAsync(stream, _pool.Limits.BufferedBodyBytes, timeout.Token).ConfigureAwait(false) is not null)
            {
                // A unary call answers once; anything more is drained and dropped.
            }

            result = GrpcResult(response, message);
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            result = new WasmCalloutResult { GrpcStatus = 4, Message = "deadline exceeded" };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
        {
            result = new WasmCalloutResult { GrpcStatus = 14, Message = ex.Message };
        }
        finally
        {
            request.Dispose();
            Interlocked.Decrement(ref _inFlight);
        }

        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        if (result.GrpcStatus == 0 && message is not null)
        {
            Deliver(vm, contextId, result, last: true, "proxy_on_grpc_receive", () => [contextId, token, result.Body.Length]);
        }
        else
        {
            Deliver(vm, contextId, result, last: true, "proxy_on_grpc_close", () => [contextId, token, result.GrpcStatus]);
        }
    }

    private async Task RunGrpcStreamAsync(WasmVm vm, int contextId, int token, HttpRequestMessage request, GrpcStreamState state)
    {
        var status = new WasmCalloutResult { GrpcStatus = 14, Message = "stream failed" };
        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, state.Token).ConfigureAwait(false);
            var initial = new List<KeyValuePair<string, string>>();
            AddPairs(initial, response.Headers);
            var metadata = new WasmCalloutResult { Headers = new WasmHeaderMap(initial) };
            Deliver(vm, contextId, metadata, last: false, "proxy_on_grpc_receive_initial_metadata", () => [contextId, token, initial.Count]);

            var stream = await response.Content.ReadAsStreamAsync(state.Token).ConfigureAwait(false);
            while (await ReadFrameAsync(stream, _pool.Limits.BufferedBodyBytes, state.Token).ConfigureAwait(false) is { } message)
            {
                var received = new WasmCalloutResult { Body = message };
                Deliver(vm, contextId, received, last: false, "proxy_on_grpc_receive", () => [contextId, token, message.Length]);
            }

            var trailers = new List<KeyValuePair<string, string>>();
            AddPairs(trailers, response.TrailingHeaders);
            var trailing = new WasmCalloutResult { Trailers = new WasmHeaderMap(trailers) };
            Deliver(vm, contextId, trailing, last: false, "proxy_on_grpc_receive_trailing_metadata", () => [contextId, token, trailers.Count]);
            status = GrpcResult(response, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
        {
            status = new WasmCalloutResult { GrpcStatus = ex is OperationCanceledException ? 1 : 14, Message = ex.Message };
        }
        finally
        {
            request.Dispose();
            state.Outgoing.Writer.TryComplete();
            Interlocked.Decrement(ref _inFlight);
        }

        // A stream the guest cancelled says nothing more; otherwise it closes with the server's status.
        if (_streams.TryRemove(token, out _) && !_shutdown.IsCancellationRequested)
        {
            Deliver(vm, contextId, status, last: true, "proxy_on_grpc_close", () => [contextId, token, status.GrpcStatus]);
        }
        else if (!_shutdown.IsCancellationRequested)
        {
            _pool.Deliver(vm, contextId, context =>
            {
                if (context is WasmHttpContext http)
                {
                    http.PendingWork = Math.Max(0, http.PendingWork - 1);
                }
            });
        }
    }

    private static HttpRequestMessage GrpcRequest(Uri origin, string serviceName, string method, List<KeyValuePair<string, string>> metadata, int timeoutMs)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/" + serviceName + "/" + method))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        CopyHeaders(metadata, request);
        request.Headers.TryAddWithoutValidation("te", "trailers");
        if (timeoutMs > 0)
        {
            request.Headers.TryAddWithoutValidation("grpc-timeout", timeoutMs.ToString(CultureInfo.InvariantCulture) + "m");
        }

        return request;
    }

    private static WasmCalloutResult GrpcResult(HttpResponseMessage response, byte[]? message)
    {
        var headers = new List<KeyValuePair<string, string>>();
        AddPairs(headers, response.Headers);
        var trailers = new List<KeyValuePair<string, string>>();
        AddPairs(trailers, response.TrailingHeaders);
        var statusText = Find(trailers, "grpc-status") ?? Find(headers, "grpc-status");
        int status;
        if (statusText is not null)
        {
            status = int.TryParse(statusText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 2;
        }
        else
        {
            status = response.StatusCode == HttpStatusCode.OK ? 2 : 14;
        }

        var text = Find(trailers, "grpc-message") ?? Find(headers, "grpc-message");
        return new WasmCalloutResult
        {
            Headers = new WasmHeaderMap(headers),
            Trailers = new WasmHeaderMap(trailers),
            Body = message ?? [],
            GrpcStatus = status,
            Message = text is null ? string.Empty : Uri.UnescapeDataString(text),
        };
    }

    private static void CopyHeaders(List<KeyValuePair<string, string>> headers, HttpRequestMessage request)
    {
        foreach (var (name, value) in headers)
        {
            if (name.StartsWith(':') || HopByHop.Contains(name))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content ??= new ByteArrayContent([]);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    private static void AddPairs(List<KeyValuePair<string, string>> pairs, HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            foreach (var value in header.Value)
            {
                pairs.Add(new(header.Key.ToLowerInvariant(), value));
            }
        }
    }

    private static string? Find(List<KeyValuePair<string, string>> pairs, string name)
    {
        foreach (var (key, value) in pairs)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static TimeSpan CalloutTimeout(int timeoutMs) =>
        TimeSpan.FromMilliseconds(timeoutMs > 0 ? Math.Min(timeoutMs, Abstractions.Plugins.ProxyPluginContract.MaxTimeoutMilliseconds)
            : Abstractions.Plugins.ProxyPluginContract.MaxTimeoutMilliseconds);

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new InvalidDataException($"callout response exceeds the {limit}-byte buffered-body limit");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Reads one length-prefixed gRPC message, or null at a clean end of stream.</summary>
    private static async Task<byte[]?> ReadFrameAsync(Stream stream, long limit, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        var read = 0;
        while (read < header.Length)
        {
            var n = await stream.ReadAsync(header.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return read == 0 ? null : throw new InvalidDataException("gRPC frame header was cut short");
            }

            read += n;
        }

        if (header[0] != 0)
        {
            throw new InvalidDataException("compressed gRPC messages are not supported");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (length > limit)
        {
            throw new InvalidDataException($"gRPC message of {length} bytes exceeds the {limit}-byte buffered-body limit");
        }

        var message = new byte[length];
        await stream.ReadExactlyAsync(message, cancellationToken).ConfigureAwait(false);
        return message;
    }

    private static byte[] Frame(ReadOnlySpan<byte> message)
    {
        var frame = new byte[message.Length + 5];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Length);
        message.CopyTo(frame.AsSpan(5));
        return frame;
    }

    private sealed class GrpcStreamState
    {
        private readonly CancellationTokenSource _cancel;
        private readonly long _limit;
        private long _pending;

        public GrpcStreamState(CancellationToken shutdown, long limit)
        {
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            _limit = limit;
        }

        public Channel<byte[]> Outgoing { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        public CancellationToken Token => _cancel.Token;

        /// <summary>Queues a message unless the unsent bytes would pass the buffered-body limit.</summary>
        public bool TryWrite(byte[] message)
        {
            if (Interlocked.Add(ref _pending, message.Length) > _limit)
            {
                Interlocked.Add(ref _pending, -message.Length);
                return false;
            }

            if (!Outgoing.Writer.TryWrite(Frame(message)))
            {
                Interlocked.Add(ref _pending, -message.Length);
                return false;
            }

            return true;
        }

        public void Sent(int length) => Interlocked.Add(ref _pending, -length);

        public void Cancel()
        {
            Outgoing.Writer.TryComplete();
            _cancel.Cancel();
        }
    }

    /// <summary>A request body that writes each queued gRPC frame as the guest sends it, until the guest half-closes.</summary>
    private sealed class GrpcStreamContent : HttpContent
    {
        private readonly GrpcStreamState _state;

        public GrpcStreamContent(GrpcStreamState state)
        {
            _state = state;
            Headers.ContentType = new MediaTypeHeaderValue(GrpcContentType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _state.Token);
            await foreach (var frame in _state.Outgoing.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                await stream.WriteAsync(frame, linked.Token).ConfigureAwait(false);
                await stream.FlushAsync(linked.Token).ConfigureAwait(false);
                _state.Sent(frame.Length - 5);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
