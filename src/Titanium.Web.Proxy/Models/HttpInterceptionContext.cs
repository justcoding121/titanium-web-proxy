using System;
using System.Net;

namespace Titanium.Web.Proxy.Models;

/// <summary>
/// Minimal, read-only context passed to <see cref="ProxyServer.ShouldInterceptHttp"/>
/// to let callers route requests to the fast-forward path or the full interception path
/// without materialising a <see cref="Titanium.Web.Proxy.EventArguments.SessionEventArgs"/>.
/// </summary>
public readonly struct HttpInterceptionContext
{
    /// <summary>Target hostname (from Host / :authority), no port, no userinfo.</summary>
    public string Hostname { get; init; }

    /// <summary>Target port (80, 443, or explicit).</summary>
    public int Port { get; init; }

    /// <summary>True when the connection is over TLS.</summary>
    public bool IsHttps { get; init; }

    /// <summary>HTTP method (GET, POST, CONNECT, …).</summary>
    public string Method { get; init; }

    /// <summary>Path and query only (no scheme/authority).</summary>
    public string PathAndQuery { get; init; }

    /// <summary>HTTP version (1.1 / 2.0 / 3.0).</summary>
    public Version HttpVersion { get; init; }

    /// <summary>The proxy endpoint that accepted this connection.</summary>
    public ProxyEndPoint ProxyEndPoint { get; init; }

    /// <summary>Remote IP endpoint of the connected client (null when unavailable).</summary>
    public IPEndPoint? ClientRemoteEndPoint { get; init; }

    /// <summary>
    ///     Process ID of the local client when available (Windows/Linux/macOS explicit-proxy paths).
    ///     Null when unset on the fast path or when the client is remote / unresolved.
    /// </summary>
    public int? ClientProcessId { get; init; }
}
