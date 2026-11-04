using System;
using System.Collections.Generic;
using System.Net;
using Titanium.Web.Proxy.Abstractions.Caching;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Caching;

/// <summary>
/// The single per-session slot the response cache hangs its state on, so a session carries one reference that is
/// null whenever the cache is off. Disposing the session releases any waiters and the staging buffer.
/// </summary>
internal sealed class CacheSessionSlot : IDisposable
{
    public ICacheRequestState? State;
    public ICacheBodyTee? Tee;
    public StorePlanHolder? Plan;

    /// <summary>The cache state a terminate-lite request carries out of the middleware chain, if any.</summary>
    public static ICacheRequestState? StateOf(ProxyMiddlewareContext context) =>
        context.Items.TryGetValue(ResponseCacheItems.StateKey, out var value) ? value as ICacheRequestState : null;

    /// <summary>Adds If-None-Match / If-Modified-Since from the stale entry to the request headed for the origin.</summary>
    public static void ApplyConditionalHeaders(ICacheRequestState st, HeaderCollection headers)
    {
        var (etag, modified) = st.Owner.ConditionalValidators(st);
        if (etag is not null)
        {
            headers.RemoveHeader("If-None-Match");
            headers.AddHeader("If-None-Match", etag);
        }

        if (modified is not null)
        {
            headers.RemoveHeader("If-Modified-Since");
            headers.AddHeader("If-Modified-Since", modified);
        }
    }

    /// <summary>Name/value snapshot of origin headers for <see cref="IResponseCacheEngine.OnOriginResponse"/>.</summary>
    public static List<KeyValuePair<string, string>> Snapshot(IEnumerable<HttpHeader> headers)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var h in headers)
        {
            list.Add(new KeyValuePair<string, string>(h.Name, h.Value));
        }

        return list;
    }

    /// <summary>Answer a session with a cached response. Framing headers come from the cache; the body is already wire-encoded.</summary>
    public static void RespondFromCache(SessionEventArgs session, CachedServe serve, bool closeServerConnection)
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
    public static void Populate(
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

    /// <summary>Start collecting the body that is about to be relayed, to store it under <paramref name="plan"/>.</summary>
    public void BeginTee(ICacheRequestState state, CacheStorePlan plan, long expectedLength)
    {
        State = state;
        Tee?.Dispose();
        Tee = state.Owner.CreateBodyTee(expectedLength);
        Plan = new StorePlanHolder { Plan = plan, ExpectedLength = expectedLength };
    }

    /// <summary>The caller already holds the whole body (a coalesced read): store it instead of using the tee.</summary>
    public void StoreBuffered(byte[] body)
    {
        var tee = Tee;
        Tee = null;
        tee?.Dispose();
        if (State is { } state && Plan is { } holder)
        {
            state.Owner.CompleteStore(state, holder.Plan, body, holder.ExpectedLength);
        }
    }

    /// <summary>Called once the body was relayed in full: store it, or drop it when it outgrew the limit.</summary>
    public void FinishStore()
    {
        var tee = Tee;
        Tee = null;
        if (tee is null || State is not { } state || Plan is not { } holder)
        {
            tee?.Dispose();
            return;
        }

        byte[]? body;
        try
        {
            body = tee.ToArray();
        }
        finally
        {
            tee.Dispose();
        }

        state.Owner.CompleteStore(state, holder.Plan, body, holder.ExpectedLength);
    }

    public void Dispose()
    {
        Tee?.Dispose();
        Tee = null;
        State?.Abandon();
    }
}

/// <summary>Carries the store plan between the response-header decision and the end of the body copy.</summary>
internal sealed class StorePlanHolder
{
    public required CacheStorePlan Plan { get; init; }
    public long ExpectedLength { get; init; } = -1;
}
