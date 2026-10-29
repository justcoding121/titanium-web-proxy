using System;
using System.Collections.Generic;
using System.Diagnostics;
using Titanium.Web.Proxy.Abstractions.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Diagnostics;

/// <summary>
///     Spans for one proxied request, on the same <see cref="ActivitySource" /> as every other proxy
///     signal. The library never references an OpenTelemetry package: a host attaches an
///     <see cref="ActivityListener" /> (or an SDK) by source name, and until one does every method here
///     returns before it parses a header or allocates, because <see cref="ActivitySource.HasListeners" />
///     is a single volatile read.
///     <para>
///         The shape is a <c>Server</c> span for the client exchange, a <c>Client</c> span below it for the
///         upstream exchange (retries are events on it), and a <c>Client</c> span for each external
///         authorization check. Parents are always passed explicitly, never taken from
///         <see cref="Activity.Current" />, so a connection that serves many requests cannot chain one
///         request's span under another.
///     </para>
///     <para>
///         Privacy: only the method, the path (never the query), the host, the protocol, the matched route and
///         cluster and the status are recorded. Cookies, credentials and bodies are not, and the optional header
///         allow-list refuses a fixed set of secret-bearing names whatever it is given.
///     </para>
/// </summary>
internal static class ProxyTracing
{
    internal const string ServerSpanName = "twp.proxy.request";
    internal const string UpstreamSpanName = "twp.proxy.upstream";
    internal const string ExtAuthSpanName = "twp.extauth";
    internal const string RetryEventName = "twp.retry";

    private const string TraceParent = "traceparent";
    private const string TraceState = "tracestate";

    /// <summary>Names that never become attributes, even when an operator lists them.</summary>
    private static readonly HashSet<string> NeverRecorded = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "proxy-authenticate",
        "www-authenticate", "x-api-key", "x-auth-token", "x-csrf-token", "x-forwarded-client-cert",
    };

    /// <summary>True when a header name may be recorded as a span attribute.</summary>
    internal static bool IsRecordableHeader(string name) =>
        !string.IsNullOrWhiteSpace(name) && !NeverRecorded.Contains(name.Trim());

    /// <summary>
    ///     Starts the server span for <paramref name="session" />, continuing a valid incoming
    ///     <c>traceparent</c> or beginning a root the listener's sampler decides on. Returns null when nothing
    ///     is listening or the sampler drops the trace without propagation.
    /// </summary>
    internal static Activity? StartServer(ProxyServer server, SessionEventArgsBase session)
    {
        var source = ProxyMetrics.ActivitySource;
        if (!source.HasListeners())
        {
            return null;
        }

        var request = session.HttpClient.Request;
        var parent = ExtractParent(request);
        if (parent == default)
        {
            // A root must not inherit whatever span happened to be ambient where the listener was started.
            Activity.Current = null;
        }

        var activity = source.StartActivity(ServerSpanName, ActivityKind.Server, parent);
        if (activity is null)
        {
            return null;
        }

        if (activity.IsAllDataRequested)
        {
            RecordRequest(server, activity, request);
        }

        return activity;
    }

    /// <summary>Sets the matched route and cluster on the server span once routing has decided them.</summary>
    internal static void RecordRoute(Activity? activity, string routeId, string clusterId)
    {
        if (activity is null || !activity.IsAllDataRequested)
        {
            return;
        }

        activity.SetTag("twp.route", routeId);
        activity.SetTag("twp.cluster", clusterId);
    }

    /// <summary>
    ///     Starts the upstream client span below the server span and, when propagation is on, makes the
    ///     upstream request carry its context. Does nothing when the request has no server span.
    /// </summary>
    internal static void BeginUpstream(ProxyServer server, SessionEventArgsBase session, string? destinationId)
    {
        var extras = session.Extras;
        if (extras?.Activity is not { } serverSpan || extras.UpstreamActivity is not null)
        {
            return;
        }

        var request = session.HttpClient.Request;
        var activity = ProxyMetrics.ActivitySource.StartActivity(
            UpstreamSpanName, ActivityKind.Client, serverSpan.Context);
        if (activity is not null)
        {
            extras.UpstreamActivity = activity;
            if (activity.IsAllDataRequested)
            {
                RecordUpstream(session, activity, request, destinationId);
            }
        }

        if (server.PropagateTraceContext)
        {
            Inject(request, activity ?? serverSpan);
        }
    }

    /// <summary>Starts the span for one external authorization check, below the server span.</summary>
    internal static Activity? StartExtAuth(SessionEventArgsBase session)
    {
        if (session.Extras?.Activity is not { } serverSpan)
        {
            return null;
        }

        return ProxyMetrics.ActivitySource.StartActivity(ExtAuthSpanName, ActivityKind.Client, serverSpan.Context);
    }

    /// <summary>Finishes an external authorization span with the verdict. Safe on null.</summary>
    internal static void EndExtAuth(Activity? activity, bool allowed, int status)
    {
        if (activity is null)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag("twp.extauth.allowed", allowed);
            if (status > 0)
            {
                activity.SetTag("http.response.status_code", status);
            }

            if (!allowed && status >= 500)
            {
                activity.SetStatus(ActivityStatusCode.Error);
            }
        }

        activity.Stop();
    }

    /// <summary>Adds a retry event to the upstream span (or the server span when there is none).</summary>
    internal static void RecordRetry(SessionEventArgsBase session, int attempt, bool timedOut, int status)
    {
        var extras = session.Extras;
        var target = extras?.UpstreamActivity ?? extras?.Activity;
        if (target is null || !target.IsAllDataRequested)
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            { "twp.retry.attempt", attempt },
            { "twp.retry.reason", timedOut ? "timeout" : "status" },
            { "http.response.status_code", status },
        };
        if (session.UpstreamDestinationId is { } destination)
        {
            tags.Add("twp.destination", destination);
        }

        target.AddEvent(new ActivityEvent(RetryEventName, tags: tags));
    }

    /// <summary>
    ///     Records the outcome on both spans and stops them, client first. Runs once per request from the
    ///     single after-response chokepoint; a second call finds them already stopped.
    /// </summary>
    internal static void Finish(RoutingFeatureState extras, int status, ForwarderError error)
    {
        Complete(extras.UpstreamActivity, status, error, clientSpan: true);
        Complete(extras.Activity, status, error, clientSpan: false);
    }

    private static void Complete(Activity? activity, int status, ForwarderError error, bool clientSpan)
    {
        if (activity is null || activity.IsStopped)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            if (status > 0)
            {
                activity.SetTag("http.response.status_code", status);
            }

            var failed = error != ForwarderError.None || status >= (clientSpan ? 400 : 500);
            if (error != ForwarderError.None)
            {
                activity.SetTag("error.type", error.ToString());
            }
            else if (failed)
            {
                activity.SetTag("error.type", status.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (failed)
            {
                activity.SetStatus(ActivityStatusCode.Error);
            }
        }

        activity.Stop();
    }

    private static ActivityContext ExtractParent(Request request)
    {
        var headers = request.Headers;
        var traceparent = headers.GetHeaderValueOrNull(TraceParent);
        if (string.IsNullOrEmpty(traceparent))
        {
            return default;
        }

        // A malformed traceparent starts a new trace instead of failing the request (W3C Trace Context).
        return ActivityContext.TryParse(traceparent, JoinedTraceState(headers), isRemote: true, out var context)
            ? context
            : default;
    }

    private static string? JoinedTraceState(HeaderCollection headers)
    {
        var lines = headers.GetHeaders(TraceState);
        if (lines is null || lines.Count == 0)
        {
            return null;
        }

        if (lines.Count == 1)
        {
            return lines[0].Value;
        }

        return string.Join(',', lines.ConvertAll(static h => h.Value));
    }

    private static void Inject(Request request, Activity activity)
    {
        if (activity.IdFormat != ActivityIdFormat.W3C || activity.Id is not { } id)
        {
            return;
        }

        request.Headers.SetOrAddHeaderValue(TraceParent, id);
        if (activity.TraceStateString is { Length: > 0 } state)
        {
            request.Headers.SetOrAddHeaderValue(TraceState, state);
        }
        else
        {
            request.Headers.RemoveHeader(TraceState);
        }
    }

    private static void RecordRequest(ProxyServer server, Activity activity, Request request)
    {
        activity.SetTag("http.request.method", request.Method);
        activity.SetTag("url.path", PathWithoutQuery(request.RequestUriString));
        activity.SetTag("url.scheme", request.IsHttps ? "https" : "http");
        var (host, port) = request.GetOriginHostPort(request.IsHttps ? 443 : 80);
        if (host.Length > 0)
        {
            activity.SetTag("server.address", host);
            activity.SetTag("server.port", port);
        }

        activity.SetTag("network.protocol.name", "http");
        activity.SetTag("network.protocol.version", ProtocolVersion(request));

        if (server.TracingHeaderAllowList is { Count: > 0 } allowed)
        {
            RecordHeaders(activity, request, allowed);
        }
    }

    private static void RecordUpstream(
        SessionEventArgsBase session, Activity activity, Request request, string? destinationId)
    {
        var host = session.UpstreamConnectHost;
        var port = session.UpstreamConnectPort;
        if (string.IsNullOrEmpty(host))
        {
            (host, var fallbackPort) = request.GetOriginHostPort(request.IsHttps ? 443 : 80);
            port = fallbackPort;
        }

        if (!string.IsNullOrEmpty(host))
        {
            activity.SetTag("server.address", host);
            if (port is { } p)
            {
                activity.SetTag("server.port", p);
            }
        }

        activity.SetTag("http.request.method", request.Method);
        if (destinationId is not null)
        {
            activity.SetTag("twp.destination", destinationId);
        }

        if (session.MatchedRoute is { } route)
        {
            activity.SetTag("twp.route", route.Id);
            activity.SetTag("twp.cluster", route.ClusterId);
        }
    }

    private static void RecordHeaders(Activity activity, Request request, IReadOnlyList<string> allowed)
    {
        for (var i = 0; i < allowed.Count; i++)
        {
            var name = allowed[i];
            if (!IsRecordableHeader(name) || request.Headers.GetHeaders(name) is not { Count: > 0 } values)
            {
                continue;
            }

            var value = values.Count == 1 ? values[0].Value : string.Join(", ", values.ConvertAll(static h => h.Value));
            activity.SetTag("http.request.header." + name.Trim().ToLowerInvariant(), value);
        }
    }

    private static string ProtocolVersion(Request request) =>
        request.HttpVersion.Major switch
        {
            >= 3 => "3",
            2 => "2",
            _ => request.HttpVersion.Minor == 0 ? "1.0" : "1.1",
        };

    /// <summary>The target's path with scheme, authority, query and fragment removed.</summary>
    internal static string PathWithoutQuery(string target)
    {
        var span = target.AsSpan();
        var scheme = span.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            var afterAuthority = span[(scheme + 3)..].IndexOfAny('/', '?', '#');
            span = afterAuthority < 0 ? default : span[(scheme + 3 + afterAuthority)..];
        }

        var end = span.IndexOfAny('?', '#');
        if (end >= 0)
        {
            span = span[..end];
        }

        return span.IsEmpty ? "/" : span.ToString();
    }
}
