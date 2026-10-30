using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Caching;

/// <summary>
/// Shared GET/HEAD response cache implementing an RFC 9111 subset (see <see cref="HttpCachePolicy"/>): freshness from
/// s-maxage, max-age, Expires and Age (plus opt-in heuristic freshness), <c>Vary</c> secondary keys, conditional
/// revalidation with 304 merge, stale-while-revalidate, stale-if-error, and request collapsing of concurrent misses.
/// Only allocated when placed in the middleware list; otherwise the hot path pays nothing.
/// </summary>
/// <remarks>
/// Safety rules that never loosen: nothing is stored for a response with <c>Set-Cookie</c>, <c>Cache-Control</c>
/// private / no-store / no-cache, or <c>Vary: *</c>; nothing is stored or served for a request carrying
/// <c>Authorization</c> or <c>Cookie</c> (unless explicitly allowed); the scheme, host, path and query, the normalized
/// Accept-Encoding and the headers a response names in <c>Vary</c> are the only things that select an entry.
/// </remarks>
public sealed class HttpResponseCacheMiddleware : IProxyMiddleware
{
    /// <summary>Largest body the H1 terminate-lite path coalesces without a streaming tee.</summary>
    internal const int MaxCachedBodyBytes = 64 * 1024;

    internal const string StateItemKey = "twp.cache.state";

    private readonly IHttpResponseCache _cache;
    private readonly ResponseCacheOptions _options;
    private readonly RequestCollapser? _collapser;

    public HttpResponseCacheMiddleware(
        IHttpResponseCache cache,
        TimeSpan? defaultTtl = null,
        bool allowAuthenticated = false,
        ResponseCacheOptions? options = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        var o = options ?? new ResponseCacheOptions();
        _options = new ResponseCacheOptions
        {
            MaxEntryBytes = Math.Max(1, o.MaxEntryBytes),
            DefaultTtl = defaultTtl is { } t && t > TimeSpan.Zero ? t : o.DefaultTtl,
            HeuristicFreshness = o.HeuristicFreshness,
            ExposeHeaders = o.ExposeHeaders,
            AllowAuthenticated = allowAuthenticated || o.AllowAuthenticated,
            StaleRetention = o.StaleRetention < TimeSpan.Zero ? TimeSpan.Zero : o.StaleRetention,
            Collapse = o.Collapse,
            CollapseMaxWaiters = o.CollapseMaxWaiters,
            CollapseTimeout = o.CollapseTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : o.CollapseTimeout,
            CollapseMaxFlights = o.CollapseMaxFlights,
        };
        if (_options.Collapse && _options.CollapseMaxWaiters > 0)
        {
            _collapser = new RequestCollapser(_options.CollapseMaxWaiters, _options.CollapseMaxFlights);
        }
    }

    internal int MaxEntryBytes => _options.MaxEntryBytes;

    internal bool ExposeHeaders => _options.ExposeHeaders;

    internal void ReleaseFlight(RequestCollapser.Flight flight) => _collapser?.Complete(flight);

    /// <summary>Origin fetches currently in flight (collapsing leaders). For tests and diagnostics.</summary>
    internal int InFlightFetches => _collapser?.InFlight ?? 0;

    public async ValueTask InvokeAsync( // NOSONAR S3776 -- Cache hit, stale-while-revalidate, and collapse paths share one request state; splitting would duplicate attach/abandon rules.
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        if (!TryCreateState(context, out var st))
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var bypass = BypassReason(st);
        if (bypass is not null)
        {
            ProxyMetrics.RecordCacheRequest("bypass");
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!Find(st, out var entry, out var entryKey))
        {
            ProxyMetrics.RecordCacheRequest("bypass");
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        st.EntryKey = entryKey;
        if (entry is not null)
        {
            var age = HttpCachePolicy.Evaluate(entry, now);
            var decision = HttpCachePolicy.Decide(entry, age, st.Directives);
            switch (decision)
            {
                case HttpCachePolicy.CacheDecision.Hit:
                    ProxyMetrics.RecordCacheRequest("hit");
                    ServeEntry(context, st, entry, age, "HIT");
                    return;

                case HttpCachePolicy.CacheDecision.ServeStale:
                    if (st.HasClientValidators || _collapser is null
                        || !_collapser.TryLead(RevalidationKey(entryKey), out var revalidation))
                    {
                        ProxyMetrics.RecordCacheRequest("stale");
                        ServeEntry(context, st, entry, age, "STALE");
                        return;
                    }

                    // One request per stale entry revalidates while every other request is served the stale copy.
                    st.Flight = revalidation;
                    st.Stale = entry;
                    st.StaleKey = entryKey;
                    st.Conditional = HttpCachePolicy.HasValidator(entry);
                    break;

                case HttpCachePolicy.CacheDecision.Revalidate:
                    st.Stale = entry;
                    st.StaleKey = entryKey;
                    st.Conditional = true;
                    break;

                default:
                    st.Stale = entry;
                    st.StaleKey = entryKey;
                    break;
            }
        }

        if (st.Flight is null)
        {
            if (st.HasClientValidators)
            {
                // The client brought its own validators and nothing here can answer them: pass the request through untouched.
                st.Stale = null;
                st.Conditional = false;
                ProxyMetrics.RecordCacheRequest("bypass");
                await next(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (st.Directives.OnlyIfCached)
            {
                RespondGatewayTimeout(context);
                return;
            }

            if (_collapser is not null && !st.IsHead)
            {
                var role = _collapser.Enter(entryKey, out var flight);
                if (role == RequestCollapser.Role.Waiter)
                {
                    var finished = await RequestCollapser.WaitAsync(flight!, _options.CollapseTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    if (finished && TryServeAfterWait(context, st))
                    {
                        return;
                    }
                }
                else if (role == RequestCollapser.Role.Leader)
                {
                    st.Flight = flight;
                }
            }
        }

        Attach(context, st);
        if (st.Conditional && context.Session is SessionEventArgs session)
        {
            ApplyConditionalHeaders(st, session.HttpClient.Request.Headers);
        }

        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            st.Abandon();
            throw;
        }

        if (context.IsHandled || context.HandledStatusCode is not null)
        {
            st.Abandon();
        }
    }

    // ------------------------------------------------------------------------------------------------ request side

    private bool TryCreateState(ProxyMiddlewareContext context, out CacheRequestState st) // NOSONAR S3776 -- Session and lite terminate paths populate the same cache state shape.
    {
        st = null!;
        string method;
        string host;
        string path;
        bool isHttps;
        Func<string, IReadOnlyList<string>?> header;
        string? authorization;

        if (context.Session is SessionEventArgs session)
        {
            var request = session.HttpClient.Request;
            method = request.Method ?? "";
            if (!IsCacheableMethod(method))
            {
                return false;
            }

            host = request.Host ?? request.RequestUri?.Host ?? "";
            path = request.RequestUri?.PathAndQuery ?? "";
            if (string.IsNullOrEmpty(path))
            {
                path = request.RequestUriString8.GetString();
            }

            isHttps = request.IsHttps;
            var headers = request.Headers;
            header = name =>
            {
                var list = headers.GetHeaders(name);
                if (list is null || list.Count == 0)
                {
                    return null;
                }

                if (list.Count == 1)
                {
                    return new[] { list[0].Value };
                }

                var values = new string[list.Count];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = list[i].Value;
                }

                return values;
            };
            authorization = header("Authorization") is { Count: > 0 } a ? a[0] : null;
        }
        else if (context.Request is { } view)
        {
            method = view.Method ?? "GET";
            if (!IsCacheableMethod(method))
            {
                return false;
            }

            host = view.Host ?? "";
            path = view.Path ?? "";
            isHttps = view.IsHttps;
            header = view.GetHeaderValues ?? (static _ => null);
            authorization = view.Authorization ?? (header("Authorization") is { Count: > 0 } a ? a[0] : null);
        }
        else
        {
            return false;
        }

        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        st = new CacheRequestState(this)
        {
            IsHead = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase),
            Host = host,
            Path = path,
            IsHttps = isHttps,
            RequestHeader = header,
            Authorization = authorization,
            Cookie = header("Cookie") is { Count: > 0 } c ? c[0] : null,
            AcceptEncoding = JoinedValue(header("Accept-Encoding")),
            Directives = HttpCachePolicy.RequestDirectives.From(header),
        };
        st.PrimaryKey = BuildPrimaryKey(host, path, isHttps);
        return true;
    }

    private string? BypassReason(CacheRequestState st)
    {
        if (!IsRequestStoreable(st.Authorization, st.Cookie))
        {
            return "authenticated";
        }

        if (st.RequestHeader("Range") is { Count: > 0 } || st.RequestHeader("If-Range") is { Count: > 0 })
        {
            return "range";
        }

        if (st.Directives.NoStore)
        {
            return "no-store";
        }

        if (st.RequestHeader("If-Match") is { Count: > 0 } || st.RequestHeader("If-Unmodified-Since") is { Count: > 0 })
        {
            return "precondition";
        }

        if (!HttpCachePolicy.IsKeySafe(st.Host) || !HttpCachePolicy.IsKeySafe(st.Path))
        {
            return "unsafe-key";
        }

        st.HasClientValidators = st.RequestHeader("If-None-Match") is { Count: > 0 }
                                 || st.RequestHeader("If-Modified-Since") is { Count: > 0 };
        return null;
    }

    private bool Find(CacheRequestState st, out CachedHttpResponse? entry, out string entryKey)
    {
        IReadOnlyList<string>? vary = null;
        if (_cache.TryGet(MarkerKey(st.PrimaryKey), out var marker) && marker?.VaryNames is { } names)
        {
            vary = names;
        }

        var secondary = HttpCachePolicy.BuildSecondaryKey(st.AcceptEncoding, vary, st.RequestHeader);
        if (secondary is null)
        {
            entry = null;
            entryKey = string.Empty;
            return false;
        }

        entryKey = st.PrimaryKey + HttpCachePolicy.KeySeparator + secondary;
        if (_cache.TryGet(entryKey, out var found) && found is { StatusCode: > 0 })
        {
            entry = found;
        }
        else
        {
            entry = null;
        }

        return true;
    }

    /// <summary>After a leader finished: serve what it stored if that satisfies this request, else fetch independently.</summary>
    private bool TryServeAfterWait(ProxyMiddlewareContext context, CacheRequestState st)
    {
        if (!Find(st, out var entry, out var entryKey) || entry is null)
        {
            return false;
        }

        var age = HttpCachePolicy.Evaluate(entry, DateTimeOffset.UtcNow);
        var decision = HttpCachePolicy.Decide(entry, age, st.Directives);
        if (decision is not (HttpCachePolicy.CacheDecision.Hit or HttpCachePolicy.CacheDecision.ServeStale))
        {
            return false;
        }

        st.EntryKey = entryKey;
        ProxyMetrics.RecordCacheRequest(decision == HttpCachePolicy.CacheDecision.Hit ? "hit" : "stale");
        ServeEntry(context, st, entry, age, decision == HttpCachePolicy.CacheDecision.Hit ? "HIT" : "STALE");
        return true;
    }

    internal bool IsRequestStoreable(string? authorization, string? cookie)
    {
        if (_options.AllowAuthenticated)
        {
            return true;
        }

        return string.IsNullOrEmpty(authorization) && string.IsNullOrEmpty(cookie);
    }

    private static string? JoinedValue(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        if (values.Count == 1)
        {
            return values[0];
        }

        return string.Join(", ", values);
    }

    private static bool IsCacheableMethod(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------------ keys

    /// <summary>
    /// Key for a stored variant: scheme, host, path and query, then the normalized Accept-Encoding and, when the
    /// resource is known to vary, each named header. HEAD uses the GET key (HEAD is answered from GET entries).
    /// </summary>
    internal static string BuildCacheKey(string method, string host, string path, string? acceptEncoding = null,
        bool isHttps = false, IReadOnlyList<string>? varyNames = null,
        Func<string, IReadOnlyList<string>?>? requestHeader = null)
    {
        _ = method;
        var secondary = HttpCachePolicy.BuildSecondaryKey(acceptEncoding, varyNames, requestHeader) ?? "ae=";
        return BuildPrimaryKey(host, path, isHttps) + HttpCachePolicy.KeySeparator + secondary;
    }

    internal static string BuildPrimaryKey(string host, string path, bool isHttps)
    {
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        return string.Concat("GET:", isHttps ? "https://" : "http://", host.ToLowerInvariant(), path);
    }

    private static string MarkerKey(string primaryKey) => primaryKey + HttpCachePolicy.KeySeparator + "vary";

    private static string RevalidationKey(string entryKey) => "R" + HttpCachePolicy.KeySeparator + entryKey;

    // ------------------------------------------------------------------------------------------------ serving

    private void ServeEntry(
        ProxyMiddlewareContext context,
        CacheRequestState st,
        CachedHttpResponse entry,
        HttpCachePolicy.EntryAge age,
        string xCache)
    {
        var conditional = HttpCachePolicy.EvaluateClientConditional(entry, st.RequestHeader);
        var serve = BuildServe(entry, entry.Headers, entry.Body, age.AgeSeconds, xCache, st.IsHead,
            conditional == HttpCachePolicy.ClientConditional.NotModified);
        Handle(context, serve);
    }

    internal CachedServe BuildServe(
        CachedHttpResponse entry,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        byte[] body,
        long ageSeconds,
        string xCache,
        bool isHead,
        bool notModified)
    {
        var list = new List<KeyValuePair<string, string>>(headers.Count + 2);
        foreach (var h in headers)
        {
            if (h.Key.Equals("Age", StringComparison.OrdinalIgnoreCase) ||
                h.Key.Equals("X-Cache", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (notModified && !IsKeptOn304(h.Key))
            {
                continue;
            }

            list.Add(h);
        }

        if (_options.ExposeHeaders)
        {
            list.Add(new KeyValuePair<string, string>("Age",
                Math.Min(ageSeconds, int.MaxValue).ToString(CultureInfo.InvariantCulture)));
            list.Add(new KeyValuePair<string, string>("X-Cache", xCache));
        }

        var status = notModified ? 304 : entry.StatusCode;
        return new CachedServe
        {
            StatusCode = status,
            Headers = list,
            Body = isHead || notModified || status == 204 ? [] : body,
        };
    }

    private static bool IsKeptOn304(string name) =>
        name.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Location", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Date", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ETag", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Expires", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Vary", StringComparison.OrdinalIgnoreCase);

    private static void Handle(ProxyMiddlewareContext context, CachedServe serve)
    {
        if (context.Session is SessionEventArgs session)
        {
            RespondFromCache(session, serve, closeServerConnection: false);
        }

        context.HandledStatusCode = serve.StatusCode;
        context.HandledBodyBytes = serve.Body;
        context.HandledHeaders = serve.Headers;
        context.HandledIsCacheHit = true;
        context.IsHandled = true;
    }

    /// <summary>Answer a session with a cached response. Framing headers come from the cache; the body is already wire-encoded.</summary>
    internal static void RespondFromCache(SessionEventArgs session, CachedServe serve, bool closeServerConnection)
    {
        var response = new GenericResponse((HttpStatusCode)serve.StatusCode);
        response.HttpVersion = session.HttpClient.Request.HttpVersion;
        Populate(response, serve.Headers, serve.Body);
        session.Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Fill a response from cached headers and an already wire-encoded body. The body goes first because assigning it
    /// stamps a Content-Length; the stored one then replaces that, so a HEAD or 304 answer keeps the representation's
    /// length with an empty body, and the body is never compressed a second time.
    /// </summary>
    internal static void Populate(
        Response response,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        byte[] body)
    {
        response.Body = body;
        response.BodyIsWireEncoded = true;
        response.Headers.RemoveHeader("Content-Length");
        foreach (var h in headers)
        {
            response.Headers.AddHeader(h.Key, h.Value);
        }
    }

    private static void RespondGatewayTimeout(ProxyMiddlewareContext context)
    {
        if (context.Session is SessionEventArgs session)
        {
            session.GenericResponse("Gateway Timeout", HttpStatusCode.GatewayTimeout);
        }

        context.HandledStatusCode = (int)HttpStatusCode.GatewayTimeout;
        context.HandledBody = "Gateway Timeout";
        context.IsHandled = true;
    }

    // ------------------------------------------------------------------------------------------------ state plumbing

    private static void Attach(ProxyMiddlewareContext context, CacheRequestState st)
    {
        context.Items[StateItemKey] = st;
        if (context.Session is SessionEventArgs session)
        {
            session.ResponseCache = new CacheSessionSlot { State = st };
        }
    }

    /// <summary>The cache state a terminate-lite request carries out of the middleware chain, if any.</summary>
    internal static CacheRequestState? StateOf(ProxyMiddlewareContext context) =>
        context.Items.TryGetValue(StateItemKey, out var value) ? value as CacheRequestState : null;

    /// <summary>Adds If-None-Match / If-Modified-Since from the stale entry to the request headed for the origin.</summary>
    internal void ApplyConditionalHeaders(CacheRequestState st, HeaderCollection headers) // NOSONAR S2325 -- Terminate-lite forwards through the middleware instance registered on the pipeline.
    {
        if (st.Stale is not { } stale || !st.Conditional)
        {
            return;
        }

        if (HttpCachePolicy.ETagOf(stale) is { } etag)
        {
            headers.RemoveHeader("If-None-Match");
            headers.AddHeader("If-None-Match", etag);
        }

        if (HttpCachePolicy.LastModifiedOf(stale) is { } modified)
        {
            headers.RemoveHeader("If-Modified-Since");
            headers.AddHeader("If-Modified-Since", modified);
        }
    }

    // ------------------------------------------------------------------------------------------------ response side

    internal enum OriginAction
    {
        /// <summary>Relay the origin response untouched.</summary>
        Pass,

        /// <summary>Send <see cref="OriginDecision.Serve"/> instead of the origin response.</summary>
        Serve,

        /// <summary>The body is small and known: read it whole, then call <see cref="CompleteStore"/>.</summary>
        StoreBuffered,

        /// <summary>The body streams: tee it while relaying, then call <see cref="CompleteStore"/> with the result.</summary>
        StoreStreamed,
    }

    internal readonly struct OriginDecision
    {
        public OriginAction Action { get; init; }
        public CachedServe? Serve { get; init; }
        public HttpCachePolicy.StorePlan? Plan { get; init; }
        public long ContentLength { get; init; }

        public static OriginDecision Pass => default;
    }

    /// <summary>
    /// Called with an origin response's status line and headers, before any of the body is relayed. Decides whether to
    /// substitute a cached response (304 revalidation, stale-if-error) or to keep the body for storing.
    /// </summary>
    internal OriginDecision OnOriginResponse(
        CacheRequestState st,
        int statusCode,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        bool hasBody,
        long contentLength,
        bool chunked)
    {
        var now = DateTimeOffset.UtcNow;
        if (st.IsHead)
        {
            ProxyMetrics.RecordCacheRequest("miss");
            st.Abandon();
            return OriginDecision.Pass;
        }

        if (statusCode == 304 && st.Stale is { } stale && st.Conditional)
        {
            return RevalidatedDecision(st, stale, headers, now);
        }

        if (statusCode >= 500 && st.Stale is { } staleForError)
        {
            var age = HttpCachePolicy.Evaluate(staleForError, now);
            if (HttpCachePolicy.MayServeStaleOnError(staleForError, age, st.Directives))
            {
                ProxyMetrics.RecordCacheRequest("stale");
                st.Abandon();
                return new OriginDecision
                {
                    Action = OriginAction.Serve,
                    Serve = BuildServe(staleForError, staleForError.Headers, staleForError.Body, age.AgeSeconds, "STALE",
                        st.IsHead, notModified: false),
                };
            }
        }

        ProxyMetrics.RecordCacheRequest("miss");
        if (!HttpCachePolicy.TryPlanStore(statusCode, headers, now, _options, out var plan, out _) || plan is null)
        {
            st.Abandon();
            return OriginDecision.Pass;
        }

        if (!hasBody || contentLength == 0)
        {
            return new OriginDecision { Action = OriginAction.StoreBuffered, Plan = plan, ContentLength = 0 };
        }

        if (!chunked && contentLength > 0 && contentLength <= _options.MaxEntryBytes)
        {
            return new OriginDecision { Action = OriginAction.StoreBuffered, Plan = plan, ContentLength = contentLength };
        }

        if (chunked)
        {
            return new OriginDecision { Action = OriginAction.StoreStreamed, Plan = plan, ContentLength = -1 };
        }

        // Longer than maxEntryBytes, or no declared length (read until close): stream through without caching.
        st.Abandon();
        return OriginDecision.Pass;
    }

    private OriginDecision RevalidatedDecision(
        CacheRequestState st,
        CachedHttpResponse stale,
        IReadOnlyList<KeyValuePair<string, string>> headers304,
        DateTimeOffset now)
    {
        var merged = HttpCachePolicy.MergeNotModified(stale, headers304);

        // Freshness is recomputed from the merged fields, with the 304's own Age (the stored one is not kept).
        var planHeaders = new List<KeyValuePair<string, string>>(merged.Count + 1);
        planHeaders.AddRange(merged);
        if (HttpCachePolicy.First(headers304, "Age") is { } age304)
        {
            planHeaders.Add(new KeyValuePair<string, string>("Age", age304));
        }

        CachedHttpResponse served;
        if (HttpCachePolicy.TryPlanStore(stale.StatusCode, planHeaders, now, _options, out var plan, out _) && plan is not null)
        {
            served = HttpCachePolicy.BuildEntry(plan, stale.Body);
            if (st.StaleKey is { } key)
            {
                UpdateVariantMarker(st.PrimaryKey, plan);
                _cache.Set(key, served, TimeSpan.Zero);
            }
        }
        else
        {
            // The 304 turned the response uncacheable (for example it now says no-store): answer this request, keep nothing.
            served = new CachedHttpResponse
            {
                StatusCode = stale.StatusCode,
                Body = stale.Body,
                Headers = HttpCachePolicy.BuildStoredHeaders(planHeaders, stale.StatusCode, stale.Body.Length, now, true),
                ExpiresUtc = now,
                StoredUtc = now,
            };
            if (st.StaleKey is { } key)
            {
                _cache.Set(key, new CachedHttpResponse
                {
                    StatusCode = stale.StatusCode,
                    Body = [],
                    Headers = [],
                    ExpiresUtc = DateTimeOffset.UnixEpoch.AddTicks(1),
                }, TimeSpan.Zero);
            }
        }

        ProxyMetrics.RecordCacheRequest("revalidated");
        var age = HttpCachePolicy.Evaluate(served, now);
        st.Abandon();
        return new OriginDecision
        {
            Action = OriginAction.Serve,
            Serve = BuildServe(served, served.Headers, served.Body, age.AgeSeconds, "REVALIDATED", st.IsHead,
                notModified: false),
        };
    }

    /// <summary>Store the finished body for the request. A body whose length disagrees with a declared length is never stored.</summary>
    internal void CompleteStore(CacheRequestState st, HttpCachePolicy.StorePlan plan, byte[]? body, long expectedLength)
    {
        try
        {
            if (body is null || body.Length > _options.MaxEntryBytes || (expectedLength >= 0 && body.Length != expectedLength))
            {
                return;
            }

            var secondary = HttpCachePolicy.BuildSecondaryKey(st.AcceptEncoding, plan.VaryNames, st.RequestHeader);
            if (secondary is null)
            {
                return;
            }

            var key = st.PrimaryKey + HttpCachePolicy.KeySeparator + secondary;
            UpdateVariantMarker(st.PrimaryKey, plan);
            _cache.Set(key, HttpCachePolicy.BuildEntry(plan, body), TimeSpan.Zero);
        }
        finally
        {
            st.Abandon();
        }
    }

    private void UpdateVariantMarker(string primaryKey, HttpCachePolicy.StorePlan plan)
    {
        var markerKey = MarkerKey(primaryKey);
        var hadMarker = _cache.TryGet(markerKey, out var existing) && existing?.VaryNames is not null;
        var names = plan.VaryNames;
        if (hadMarker && !SameNames(existing!.VaryNames!, names))
        {
            // The set of varying headers changed: variants keyed under the old shape are unreachable. Drop them.
            _cache.Purge(primaryKey + HttpCachePolicy.KeySeparator);
        }

        if (names.Count == 0)
        {
            if (hadMarker)
            {
                // The resource stopped varying: replace the marker with one that is already expired.
                _cache.Set(markerKey, new CachedHttpResponse
                {
                    StatusCode = 0,
                    Body = [],
                    Headers = [],
                    ExpiresUtc = DateTimeOffset.UnixEpoch.AddTicks(1),
                }, TimeSpan.Zero);
            }

            return;
        }

        _cache.Set(markerKey, new CachedHttpResponse
        {
            StatusCode = 0,
            Body = [],
            Headers = [],
            VaryNames = names,
            ExpiresUtc = plan.ExpiresUtc,
            RetainUntilUtc = plan.RetainUntilUtc,
        }, TimeSpan.Zero);
    }

    private static bool SameNames(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------------------------------------ direct store (tests, session fill)

    /// <summary>
    /// Store a response whose body is already in memory (terminate-lite coalesce or a buffered session response).
    /// Only GET responses are stored (HEAD is answered from them). The same safety rules as the streaming path apply.
    /// </summary>
    internal void TryStore( // NOSONAR S107 -- Direct store mirrors streaming store inputs; grouping would not reduce call-site risk on tests.
        string method,
        string host,
        string path,
        int statusCode,
        IEnumerable<HttpHeader> headers,
        ReadOnlySpan<byte> body,
        string? acceptEncoding = null,
        string? authorization = null,
        string? cookie = null,
        bool isHttps = false,
        Func<string, IReadOnlyList<string>?>? requestHeader = null)
    {
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) || body.Length > _options.MaxEntryBytes)
        {
            return;
        }

        if (!IsRequestStoreable(authorization, cookie))
        {
            return;
        }

        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        if (!HttpCachePolicy.IsKeySafe(host) || !HttpCachePolicy.IsKeySafe(path))
        {
            return;
        }

        var snapshot = Snapshot(headers);
        if (HttpCachePolicy.First(snapshot, "Content-Length") is { } declared
            && long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            && length != body.Length)
        {
            return;
        }

        if (!HttpCachePolicy.TryPlanStore(statusCode, snapshot, DateTimeOffset.UtcNow, _options, out var plan, out _)
            || plan is null)
        {
            return;
        }

        var aeValues = acceptEncoding is null ? null : new[] { acceptEncoding };
        var st = new CacheRequestState(this)
        {
            IsHead = false,
            Host = host,
            Path = path,
            IsHttps = isHttps,
            AcceptEncoding = acceptEncoding,
            RequestHeader = requestHeader
                            ?? (name => name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ? aeValues : null),
            Directives = HttpCachePolicy.RequestDirectives.None,
        };
        st.PrimaryKey = BuildPrimaryKey(host, path, isHttps);
        if (plan.VaryNames.Count > 0 && requestHeader is null)
        {
            // The headers the response varies on are unknown here, so the variant cannot be keyed.
            return;
        }

        CompleteStore(st, plan, body.ToArray(), body.Length);
    }

    /// <summary>
    /// Call from the session response path when a small response is already buffered.
    /// </summary>
    public void TryCacheCurrentResponse(SessionEventArgs session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var request = session.HttpClient.Request;
        var method = request.Method ?? "";
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var response = session.HttpClient.Response;
        // A body read for a handler is decoded; cached bodies are served as the wire bytes their Content-Encoding names.
        if (!response.IsBodyRead || (response.ContentEncoding != null && !response.BodyIsWireEncoded))
        {
            return;
        }

        byte[] body;
        try
        {
            body = response.Body;
        }
        catch
        {
            return;
        }

        var host = request.Host ?? request.RequestUri?.Host ?? "";
        var path = request.RequestUri?.PathAndQuery ?? "";
        if (string.IsNullOrEmpty(path))
        {
            path = request.RequestUriString8.GetString();
        }

        var headers = request.Headers;
        TryStore(method, host, path, response.StatusCode, response.Headers, body,
            headers.GetFirstHeader("Accept-Encoding")?.Value,
            headers.GetFirstHeader("Authorization")?.Value,
            headers.GetFirstHeader("Cookie")?.Value,
            request.IsHttps,
            name =>
            {
                var list = headers.GetHeaders(name);
                if (list is null || list.Count == 0)
                {
                    return null;
                }

                var values = new string[list.Count];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = list[i].Value;
                }

                return values;
            });
    }

    internal static List<KeyValuePair<string, string>> Snapshot(IEnumerable<HttpHeader> headers)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var h in headers)
        {
            list.Add(new KeyValuePair<string, string>(h.Name, h.Value));
        }

        return list;
    }
}

/// <summary>What the cache knows about one in-flight request between the middleware and the origin response.</summary>
internal sealed class CacheRequestState
{
    private readonly HttpResponseCacheMiddleware _owner;
    private int _abandoned;

    public CacheRequestState(HttpResponseCacheMiddleware owner) => _owner = owner;

    public HttpResponseCacheMiddleware Owner => _owner;

    public required bool IsHead { get; init; }
    public required string Host { get; init; }
    public required string Path { get; init; }
    public required bool IsHttps { get; init; }
    public required Func<string, IReadOnlyList<string>?> RequestHeader { get; init; }
    public required HttpCachePolicy.RequestDirectives Directives { get; init; }
    public string? AcceptEncoding { get; init; }
    public string? Authorization { get; init; }
    public string? Cookie { get; init; }

    public string PrimaryKey { get; set; } = string.Empty;
    public string EntryKey { get; set; } = string.Empty;
    public CachedHttpResponse? Stale { get; set; }
    public string? StaleKey { get; set; }
    public bool Conditional { get; set; }
    public bool HasClientValidators { get; set; }

    public RequestCollapser.Flight? Flight { get; set; }

    /// <summary>Releases requests waiting behind this fetch. Idempotent; safe from any exit path.</summary>
    public void Abandon()
    {
        if (Interlocked.Exchange(ref _abandoned, 1) != 0)
        {
            return;
        }

        if (Flight is { } flight)
        {
            _owner.ReleaseFlight(flight);
        }
    }
}

/// <summary>A cached response ready to be written to a client.</summary>
internal sealed class CachedServe
{
    public required int StatusCode { get; init; }
    public required List<KeyValuePair<string, string>> Headers { get; init; }
    public required byte[] Body { get; init; }
}
