using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     TLS listener that terminates a WebTransport session and opens a matching session
///     on a configured HTTP origin. The path is an application resource, not a UDP target.
/// </summary>
[Experimental("TWP001")]
public sealed class WebTransportProxyEndPoint : ProxyEndPoint
{
    private readonly ConcurrentDictionary<IPAddress, int> clients = new();
    private int active;

    public WebTransportProxyEndPoint(IPAddress ipAddress, int port)
        : base(ipAddress, port, decryptSsl: true)
    {
        Options = new WebTransportOptions();
    }

    /// <summary>Uses <see cref="ProxyEndPoint.GenericCertificate"/> as the certificate clients authenticate.</summary>
    public WebTransportOptions Options { get; set; }

    internal bool TryAdmit(IPAddress client)
    {
        if (Interlocked.Increment(ref active) > Options.MaxSessions)
        {
            Interlocked.Decrement(ref active);
            return false;
        }

        var count = clients.AddOrUpdate(client, 1, static (_, current) => current + 1);
        if (count > Options.MaxSessionsPerClient)
        {
            ReleaseClient(client);
            Interlocked.Decrement(ref active);
            return false;
        }

        return true;
    }

    internal void ReleaseSession(IPAddress client)
    {
        ReleaseClient(client);
        Interlocked.Decrement(ref active);
    }

    private void ReleaseClient(IPAddress client)
    {
        var next = clients.AddOrUpdate(client, 0, static (_, current) => current > 0 ? current - 1 : 0);
        if (next == 0)
        {
            clients.TryRemove(new KeyValuePair<IPAddress, int>(client, 0));
        }
    }
}

/// <summary>Origin and admission settings for a <see cref="WebTransportProxyEndPoint"/>.</summary>
[Experimental("TWP001")]
public sealed class WebTransportOptions
{
    public bool RequireAuth { get; set; } = true;

    /// <summary>When true, the listener also offers ALPN http/1.1 and accepts Upgrade: webtransport.</summary>
    public bool AllowHttp11Upgrade { get; set; }

    /// <summary>
    ///     Listener-local basic-auth check. When null, the proxy falls back to
    ///     <c>ProxyServer.ProxyBasicAuthenticateFunc</c>. When both are null, authentication fails closed.
    /// </summary>
    public Func<string, string, Task<bool>>? AuthenticateFunc { get; set; }

    /// <summary>Pinned HTTP origin host. The client path is forwarded; the client does not choose this host.</summary>
    public string? OriginHost { get; set; }

    public int OriginPort { get; set; } = 443;

    /// <summary>When true, the origin leg is TLS. Cleartext is for a private origin the operator configures.</summary>
    public bool OriginHttps { get; set; } = true;

    public int MaxSessions { get; set; } = 1024;

    public int MaxSessionsPerClient { get; set; } = 16;
}
