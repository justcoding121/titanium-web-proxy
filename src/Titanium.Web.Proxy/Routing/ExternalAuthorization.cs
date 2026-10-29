using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Diagnostics;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Asks an authorization service before a request is forwarded (an <c>auth_request</c> / forward-auth check).
/// Only routes that list an <c>extAuth</c> policy reach this code.
/// </summary>
internal static class ExternalAuthorization
{
    /// <summary>Marks the subrequest. A request that already carries it is refused on an extAuth route.</summary>
    internal const string LoopHeader = "X-Titanium-ExtAuth";

    /// <summary>Largest answer body relayed to the client when the service refuses a request.</summary>
    internal const int MaxDenyBodyBytes = 64 * 1024;

    internal const string PolicyKind = "extAuth";

    private static readonly ConditionalWeakTable<RoutePolicyConfig, ExtAuthClient> Clients = new();

    /// <summary>
    /// Runs every <c>extAuth</c> policy of the route. Returns true when the request was refused and a response
    /// is staged. On allow, identity headers named by the policies are replaced on the upstream request.
    /// </summary>
    internal static async ValueTask<bool> DeniedAsync(
        ReverseProxyOptions options, RouteConfig route, SessionEventArgs session)
    {
        if (route.Policies is not { Count: > 0 } names || options.Policies is not { Count: > 0 } policies)
        {
            return false;
        }

        List<ExtAuthClient>? clients = null;
        for (var n = 0; n < names.Count; n++)
        {
            for (var p = 0; p < policies.Count; p++)
            {
                if (string.Equals(policies[p].Name, names[n], StringComparison.Ordinal) &&
                    string.Equals(policies[p].Kind, PolicyKind, StringComparison.Ordinal))
                {
                    (clients ??= new List<ExtAuthClient>(1)).Add(Clients.GetValue(policies[p], static c => new ExtAuthClient(c)));
                    break;
                }
            }
        }

        if (clients is null)
        {
            return false;
        }

        var request = session.HttpClient.Request;
        if (request.Headers.HeaderExists(LoopHeader))
        {
            ProxyMetrics.RecordExtAuth("deny");
            Refuse(session, new ExtAuthVerdict { Status = 508 });
            return true;
        }

        var peer = session.ClientRemoteEndPoint?.Address;
        List<KeyValuePair<string, string>>? copied = null;
        for (var i = 0; i < clients.Count; i++)
        {
            var span = ProxyTracing.StartExtAuth(session);
            ExtAuthVerdict verdict;
            try
            {
                verdict = await clients[i].CheckAsync(request, peer).ConfigureAwait(false);
            }
            catch
            {
                ProxyTracing.EndExtAuth(span, allowed: false, status: 0);
                throw;
            }

            ProxyTracing.EndExtAuth(span, verdict.Allowed, verdict.Status);
            if (!verdict.Allowed)
            {
                Refuse(session, verdict);
                return true;
            }

            if (verdict.Copy is { Count: > 0 })
            {
                (copied ??= new List<KeyValuePair<string, string>>()).AddRange(verdict.Copy);
            }
        }

        // A client-supplied header of the same name must never reach the upstream as if the service had set it.
        for (var i = 0; i < clients.Count; i++)
        {
            foreach (var name in clients[i].CopyNames)
            {
                request.Headers.RemoveHeader(name);
            }
        }

        if (copied is not null)
        {
            foreach (var pair in copied)
            {
                request.Headers.AddHeader(pair.Key, pair.Value);
            }
        }

        return false;
    }

    private static void Refuse(SessionEventArgs session, ExtAuthVerdict verdict)
    {
        RoutePolicies.ReleaseSessionSlot(session);
        session.ForwarderError = ForwarderError.PolicyDenied;
        session.GenericResponse(
            verdict.Body ?? Array.Empty<byte>(),
            (HttpStatusCode)verdict.Status,
            (IEnumerable<HttpHeader>?)verdict.Headers);
    }

    internal readonly struct ExtAuthVerdict
    {
        public bool Allowed { get; init; }
        public int Status { get; init; }
        public byte[]? Body { get; init; }
        public List<HttpHeader>? Headers { get; init; }
        /// <summary>Headers the service set. Read-only: a cached or coalesced answer is shared by many requests.</summary>
        public IReadOnlyList<KeyValuePair<string, string>>? Copy { get; init; }
    }

    /// <summary>Protection against an authorization service that is slow or down. Not part of the public configuration.</summary>
    /// <param name="MaxInFlight">Most subrequests running at once for one policy. Beyond it a request is decided by <c>failOpen</c> at once.</param>
    /// <param name="FailureThreshold">Consecutive service failures that start a cooldown.</param>
    /// <param name="Cooldown">How long requests are decided by <c>failOpen</c> without calling the service, then one probe is let through.</param>
    internal readonly record struct ExtAuthLimits(int MaxInFlight, int FailureThreshold, TimeSpan Cooldown)
    {
        public static ExtAuthLimits Default { get; } = new(256, 5, TimeSpan.FromSeconds(1));
    }

    internal sealed class ExtAuthClient
    {
        private const int DefaultTimeoutMilliseconds = 2000;
        private const int DefaultCacheMaxEntries = 10000;

        private static readonly string[] DefaultSendHeaders = ["Authorization", "Cookie"];
        private static readonly string[] DefaultDenyHeaders = ["Location", "Set-Cookie", "WWW-Authenticate", "Content-Type"];

        // Set by the proxy on every subrequest, so a configured name can never overwrite them.
        private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "X-Original-Method", "X-Original-Uri", "X-Original-Host", "X-Forwarded-For", LoopHeader,
        };

        // Hop-by-hop and framing headers belong to one connection, not to a decision.
        private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
            "Transfer-Encoding", "Upgrade", "Content-Length", "Host",
        };

        private readonly Uri? url;
        private readonly HttpMethod method;
        private readonly string[] sendHeaders;
        private readonly string[] denyHeaders;
        private readonly int timeoutMilliseconds;
        private readonly bool failOpen;
        private readonly int cacheSeconds;
        private readonly int cacheMaxEntries;
        private readonly HttpClient http;

        private readonly object gate = new();
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> cache = new(StringComparer.Ordinal);
        private readonly LinkedList<CacheEntry> cacheOrder = new();

        private readonly SemaphoreSlim inFlight;
        private readonly int failureThreshold;
        private readonly long cooldownTicks;
        private int consecutiveFailures;
        private long cooldownUntilTicks;
        private readonly Dictionary<string, Task<ExtAuthVerdict>> pending = new(StringComparer.Ordinal);

        public ExtAuthClient(RoutePolicyConfig policy)
            : this(policy, ExtAuthLimits.Default)
        {
        }

        public ExtAuthClient(RoutePolicyConfig policy, ExtAuthLimits limits)
        {
            var maxInFlight = Math.Max(limits.MaxInFlight, 1);
            inFlight = new SemaphoreSlim(maxInFlight, maxInFlight);
            MaxInFlight = maxInFlight;
            failureThreshold = Math.Max(limits.FailureThreshold, 1);
            cooldownTicks = Math.Max(limits.Cooldown.Ticks, 1);
            url = Uri.TryCreate(policy.Url, UriKind.Absolute, out var parsed) &&
                  (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                ? parsed
                : null;
            method = new HttpMethod(string.IsNullOrWhiteSpace(policy.Method) ? "GET" : policy.Method.Trim().ToUpperInvariant());
            sendHeaders = Filter(policy.SendHeaders ?? DefaultSendHeaders, rejectReserved: true);
            CopyNames = Filter(policy.CopyResponseHeaders ?? [], rejectReserved: false);
            denyHeaders = Filter(policy.DenyResponseHeaders ?? DefaultDenyHeaders, rejectReserved: false);
            timeoutMilliseconds = Math.Clamp(policy.TimeoutMilliseconds ?? DefaultTimeoutMilliseconds, 1, 60_000);
            failOpen = policy.FailOpen ?? false;
            cacheSeconds = Math.Max(policy.CacheSeconds ?? 0, 0);
            cacheMaxEntries = Math.Clamp(policy.CacheMaxEntries ?? DefaultCacheMaxEntries, 1, 1_000_000);

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
            };
            http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>Header names the service may set on the upstream request. Client copies are removed on allow.</summary>
        public string[] CopyNames { get; }

        internal int MaxInFlight { get; }

        /// <summary>Subrequests running now.</summary>
        internal int InFlight => MaxInFlight - inFlight.CurrentCount;

        private static string[] Filter(IEnumerable<string> names, bool rejectReserved)
        {
            var list = new List<string>();
            foreach (var raw in names)
            {
                var name = raw?.Trim();
                if (string.IsNullOrEmpty(name) || !IsToken(name) || ForbiddenHeaders.Contains(name) ||
                    (rejectReserved && ReservedHeaders.Contains(name)))
                {
                    continue;
                }

                if (!list.Exists(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(name);
                }
            }

            return list.ToArray();
        }

        private static bool IsToken(string name)
        {
            foreach (var c in name)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            return true;
        }

        public async ValueTask<ExtAuthVerdict> CheckAsync(Request request, IPAddress? peer)
        {
            if (url is null)
            {
                return Failure();
            }

            if (cacheSeconds <= 0)
            {
                return await FetchAsync(request, peer, cacheKey: null).ConfigureAwait(false);
            }

            var cacheKey = BuildCacheKey(request);
            if (TryGetCached(cacheKey, out var hit))
            {
                ProxyMetrics.RecordExtAuth("cache_hit");
                return new ExtAuthVerdict { Allowed = true, Copy = hit };
            }

            // Identical requests that arrive while one is being decided wait for that answer instead of each asking the service.
            TaskCompletionSource<ExtAuthVerdict>? mine = null;
            Task<ExtAuthVerdict>? shared;
            lock (gate)
            {
                if (TryGetCached(cacheKey, out hit))
                {
                    ProxyMetrics.RecordExtAuth("cache_hit");
                    return new ExtAuthVerdict { Allowed = true, Copy = hit };
                }

                if (!pending.TryGetValue(cacheKey, out shared))
                {
                    mine = new TaskCompletionSource<ExtAuthVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending[cacheKey] = mine.Task;
                }
            }

            if (shared is not null)
            {
                ProxyMetrics.RecordExtAuth("coalesced");
                return Clone(await shared.ConfigureAwait(false));
            }

            try
            {
                var verdict = await FetchAsync(request, peer, cacheKey).ConfigureAwait(false);
                mine!.TrySetResult(verdict);
                return verdict;
            }
            catch (Exception ex)
            {
                mine!.TrySetException(ex);
                throw;
            }
            finally
            {
                lock (gate)
                {
                    pending.Remove(cacheKey);
                }
            }
        }

        /// <summary>One decision from the service, behind the cooldown and the in-flight bound.</summary>
        private async ValueTask<ExtAuthVerdict> FetchAsync(Request request, IPAddress? peer, string? cacheKey)
        {
            if (InCooldown())
            {
                return Failure("cooldown");
            }

            // Never queue: a service that cannot keep up must not turn into an unbounded pile of waiting requests.
            if (!inFlight.Wait(0))
            {
                return Failure("saturated");
            }

            try
            {
                using var deadline = new CancellationTokenSource(timeoutMilliseconds);
                using var message = BuildMessage(request, peer);
                using var response = await http
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);

                var status = (int)response.StatusCode;
                if (status is >= 200 and < 300)
                {
                    var copy = Collect(response, CopyNames);
                    if (cacheKey is not null)
                    {
                        Store(cacheKey, copy);
                    }

                    NoteSuccess();
                    ProxyMetrics.RecordExtAuth("allow");
                    return new ExtAuthVerdict { Allowed = true, Copy = copy };
                }

                if (status < 200 || status >= 500)
                {
                    NoteFailure();
                    return Failure();
                }

                var body = await ReadCappedAsync(response, deadline.Token).ConfigureAwait(false);
                var relayed = new List<HttpHeader>();
                foreach (var pair in Collect(response, denyHeaders))
                {
                    relayed.Add(new HttpHeader(pair.Key, pair.Value));
                }

                NoteSuccess();
                ProxyMetrics.RecordExtAuth("deny");
                return new ExtAuthVerdict { Status = status, Body = body, Headers = relayed };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Timeout, refused connection, TLS failure, or a malformed answer: one outcome, set by FailOpen.
                NoteFailure();
                return Failure();
            }
            finally
            {
                inFlight.Release();
            }
        }

        /// <summary>
        /// True while the service is being left alone after repeated failures. Once the cooldown has run out, exactly one
        /// request is let through as a probe and the rest keep waiting out another cooldown until the probe answers.
        /// </summary>
        private bool InCooldown()
        {
            var until = Volatile.Read(ref cooldownUntilTicks);
            if (until == 0)
            {
                return false;
            }

            var now = RoutePolicies.Clock.GetUtcNow().UtcTicks;
            if (now < until)
            {
                return true;
            }

            return Interlocked.CompareExchange(ref cooldownUntilTicks, now + cooldownTicks, until) != until;
        }

        private void NoteSuccess()
        {
            if (Volatile.Read(ref cooldownUntilTicks) != 0 || Volatile.Read(ref consecutiveFailures) != 0)
            {
                Volatile.Write(ref consecutiveFailures, 0);
                Volatile.Write(ref cooldownUntilTicks, 0);
            }
        }

        private void NoteFailure()
        {
            if (Interlocked.Increment(ref consecutiveFailures) >= failureThreshold)
            {
                Volatile.Write(ref cooldownUntilTicks, RoutePolicies.Clock.GetUtcNow().UtcTicks + cooldownTicks);
            }
        }

        /// <summary>A coalesced follower gets its own copy of anything a response can still change.</summary>
        private static ExtAuthVerdict Clone(ExtAuthVerdict verdict)
        {
            if (verdict.Headers is null && verdict.Body is null)
            {
                return verdict;
            }

            List<HttpHeader>? headers = null;
            if (verdict.Headers is { } source)
            {
                headers = new List<HttpHeader>(source.Count);
                foreach (var header in source)
                {
                    headers.Add(new HttpHeader(header.Name, header.Value));
                }
            }

            return new ExtAuthVerdict
            {
                Allowed = verdict.Allowed,
                Status = verdict.Status,
                Body = (byte[]?)verdict.Body?.Clone(),
                Headers = headers,
                Copy = verdict.Copy,
            };
        }

        private ExtAuthVerdict Failure(string? reason = null)
        {
            if (failOpen)
            {
                ProxyMetrics.RecordExtAuth(reason is null ? "error_open" : reason + "_open");
                return new ExtAuthVerdict { Allowed = true };
            }

            ProxyMetrics.RecordExtAuth(reason is null ? "error_closed" : reason + "_closed");
            return new ExtAuthVerdict { Status = 503 };
        }
        private HttpRequestMessage BuildMessage(Request request, IPAddress? peer)
        {
            var message = new HttpRequestMessage(method, url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };

            // The decision call is a child of this request's trace, so the service's own spans join it.
            if (Activity.Current is { IdFormat: ActivityIdFormat.W3C } current &&
                ReferenceEquals(current.Source, ProxyMetrics.ActivitySource) && current.Id is { } traceparent)
            {
                message.Headers.TryAddWithoutValidation("traceparent", traceparent);
                if (current.TraceStateString is { Length: > 0 } tracestate)
                {
                    message.Headers.TryAddWithoutValidation("tracestate", tracestate);
                }
            }

            foreach (var name in sendHeaders)
            {
                if (request.Headers.GetHeaders(name) is not { } values)
                {
                    continue;
                }

                foreach (var header in values)
                {
                    message.Headers.TryAddWithoutValidation(name, header.Value);
                }
            }

            message.Headers.TryAddWithoutValidation("X-Original-Method", request.Method ?? "GET");
            message.Headers.TryAddWithoutValidation("X-Original-Uri", OriginalTarget(request));
            message.Headers.TryAddWithoutValidation("X-Original-Host", OriginalHost(request));
            if (peer is not null)
            {
                message.Headers.TryAddWithoutValidation("X-Forwarded-For", peer.ToString());
            }

            message.Headers.TryAddWithoutValidation(LoopHeader, "1");
            return message;
        }

        private static string OriginalHost(Request request) => request.Host ?? request.Authority.GetString();

        /// <summary>Path and query as the client addressed them, also when an explicit proxy got an absolute-form target.</summary>
        private static string OriginalTarget(Request request)
        {
            var target = request.RequestUriString;
            if (target.Length == 0 || target[0] == '/' || target == "*")
            {
                return target;
            }

            return Uri.TryCreate(target, UriKind.Absolute, out var absolute) ? absolute.PathAndQuery : target;
        }

        /// <summary>
        /// SHA-256 over what the decision can depend on: the sent header values, the method, the host, and the
        /// path plus query. The auth service sees the query in <c>X-Original-Uri</c>, so two queries on the
        /// same path must not share an allow. Lengths and presence markers keep one field from running into the next.
        /// </summary>
        private string BuildCacheKey(Request request)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, request.Method ?? "GET");
            Append(hash, OriginalHost(request));
            Append(hash, OriginalTarget(request));
            foreach (var name in sendHeaders)
            {
                if (request.Headers.GetHeaders(name) is not { } values)
                {
                    Append(hash, null);
                    continue;
                }

                Append(hash, values.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var header in values)
                {
                    Append(hash, header.Value);
                }
            }

            return Convert.ToBase64String(hash.GetHashAndReset());
        }

        private static void Append(IncrementalHash hash, string? value)
        {
            if (value is null)
            {
                hash.AppendData([0]);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> prefix = stackalloc byte[5];
            prefix[0] = 1;
            BitConverter.TryWriteBytes(prefix[1..], bytes.Length);
            hash.AppendData(prefix);
            hash.AppendData(bytes);
        }

        private bool TryGetCached(string key, out IReadOnlyList<KeyValuePair<string, string>> copy)
        {
            var now = RoutePolicies.Clock.GetUtcNow();
            lock (gate)
            {
                if (cache.TryGetValue(key, out var node))
                {
                    if (node.Value.Expires > now)
                    {
                        copy = node.Value.Copy;
                        return true;
                    }

                    cacheOrder.Remove(node);
                    cache.Remove(key);
                }
            }

            copy = Array.Empty<KeyValuePair<string, string>>();
            return false;
        }

        private void Store(string key, IReadOnlyList<KeyValuePair<string, string>> copy)
        {
            var expires = RoutePolicies.Clock.GetUtcNow().AddSeconds(cacheSeconds);
            lock (gate)
            {
                if (cache.Remove(key, out var existing))
                {
                    cacheOrder.Remove(existing);
                }

                cache[key] = cacheOrder.AddLast(new CacheEntry(key, expires, copy));
                while (cache.Count > cacheMaxEntries && cacheOrder.First is { } oldest)
                {
                    cacheOrder.RemoveFirst();
                    cache.Remove(oldest.Value.Key);
                }
            }
        }

        /// <summary>The headers a response asked for, as a read-only list: the answer may be cached and handed to many requests.</summary>
        private static IReadOnlyList<KeyValuePair<string, string>> Collect(HttpResponseMessage response, string[] names)
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (var name in names)
            {
                AddValues(result, name, response.Headers.TryGetValues(name, out var plain) ? plain : null);
                AddValues(result, name, response.Content.Headers.TryGetValues(name, out var content) ? content : null);
            }

            return result.AsReadOnly();
        }

        private static void AddValues(List<KeyValuePair<string, string>> result, string name, IEnumerable<string>? values)
        {
            if (values is null)
            {
                return;
            }

            foreach (var value in values)
            {
                if (IsSafeValue(value))
                {
                    result.Add(new KeyValuePair<string, string>(name, value));
                }
            }
        }

        private static bool IsSafeValue(string value)
        {
            foreach (var c in value)
            {
                if (c is '\r' or '\n' or '\0')
                {
                    return false;
                }
            }

            return true;
        }

        private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken token)
        {
            var declared = response.Content.Headers.ContentLength;
            var buffer = new byte[(int)Math.Clamp(declared ?? 1024, 1, MaxDenyBodyBytes)];
            var length = 0;
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                while (true)
                {
                    if (length == buffer.Length)
                    {
                        if (length >= MaxDenyBodyBytes)
                        {
                            break;
                        }

                        Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxDenyBodyBytes));
                    }

                    var read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    length += read;
                }
            }

            return length == buffer.Length ? buffer : buffer.AsSpan(0, length).ToArray();
        }

        private sealed record CacheEntry(string Key, DateTimeOffset Expires, IReadOnlyList<KeyValuePair<string, string>> Copy);
    }
}
