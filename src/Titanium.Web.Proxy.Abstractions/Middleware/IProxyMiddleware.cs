using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Abstractions.Middleware;

/// <summary>Middleware next delegate.</summary>
public delegate ValueTask ProxyMiddlewareDelegate(ProxyMiddlewareContext context, CancellationToken cancellationToken);

/// <summary>
/// Lightweight request view for middleware that runs on the H1 terminate-lite path
/// (no <c>SessionEventArgs</c>). Session-path middleware can ignore this and use <see cref="ProxyMiddlewareContext.Session"/>.
/// </summary>
public sealed class MiddlewareRequestView
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string Host { get; init; }
    public long ContentLength { get; init; }
    public string? Authorization { get; init; }

    /// <summary>Returns header values for a header name, or null when absent.</summary>
    public Func<string, IReadOnlyList<string>?>? GetHeaderValues { get; init; }
}

/// <summary>Context for <see cref="IProxyMiddleware"/> (runs inside BeforeRequest or terminate-lite).</summary>
public sealed class ProxyMiddlewareContext
{
    /// <summary>
    /// Session bag when on the full interception path; may be a sentinel on terminate-lite
    /// (use <see cref="ClientRemoteEndPoint"/> / <see cref="Request"/> / handled fields instead).
    /// </summary>
    public required object Session { get; init; }

    public bool IsHandled { get; set; }
    public Dictionary<string, object?> Items { get; } = new();

    /// <summary>Client address for CIDR / rate-limit when <see cref="Session"/> is not a session bag.</summary>
    public IPEndPoint? ClientRemoteEndPoint { get; set; }

    /// <summary>Request fields for WAF / JWT when running without a session bag.</summary>
    public MiddlewareRequestView? Request { get; set; }

    /// <summary>Set by deny helpers when no session bag can accept <c>GenericResponse</c>.</summary>
    public int? HandledStatusCode { get; set; }

    public string? HandledBody { get; set; }

    public List<KeyValuePair<string, string>>? HandledHeaders { get; set; }
}

/// <summary>
/// Optional middleware. An empty middleware list must allocate nothing on the hot path.
/// Chain terminus is the existing BeforeRequest handler list.
/// </summary>
public interface IProxyMiddleware
{
    ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken);
}
