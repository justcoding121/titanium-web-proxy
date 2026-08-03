using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

public class TestSuite : IDisposable
{
    private readonly TestServer server;
    private readonly bool ownsServer;
    private readonly ConcurrentBag<HttpClient> clients = new();
    private readonly List<ProxyServer> proxyServers = new();
    private bool disposed;

    public TestSuite(bool requireMutualTls = false)
    {
        server = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls);
        ownsServer = true;
    }

    /// <summary>
    /// Constructs a <see cref="TestSuite"/> that borrows an externally-owned <see cref="TestServer"/>.
    /// The server will NOT be disposed when this suite is disposed; only proxies and clients are.
    /// Intended for class-level fixture sharing where the server lifetime is managed by
    /// <c>[ClassInitialize]</c>/<c>[ClassCleanup]</c>.
    /// </summary>
    public TestSuite(TestServer sharedServer)
    {
        server = sharedServer;
        ownsServer = false;
    }

    public TestServer GetServer()
    {
        return server;
    }

    public ProxyServer GetProxy(ProxyServer? upStreamProxy = null)
    {
        var proxyServer = new TestProxyServer(false, upStreamProxy).ProxyServer;
        proxyServers.Add(proxyServer);
        return proxyServer;
    }

    public ProxyServer GetReverseProxy(ProxyServer? upStreamProxy = null)
    {
        var proxyServer = new TestProxyServer(true, upStreamProxy).ProxyServer;
        proxyServers.Add(proxyServer);
        return proxyServer;
    }

    public HttpClient GetClient(ProxyServer proxyServer, bool enableBasicProxyAuthorization = false)
    {
        var client = TestHelper.GetHttpClient(proxyServer.ProxyEndPoints[0].Port, enableBasicProxyAuthorization);
        clients.Add(client);
        return client;
    }

    public HttpClient GetReverseProxyClient()
    {
        var client = TestHelper.GetHttpClient();
        clients.Add(client);
        return client;
    }

    public void Dispose()
    {
        if (disposed) return;

        disposed = true;

        foreach (var client in clients)
        {
            client.Dispose();
        }

        for (var i = proxyServers.Count - 1; i >= 0; i--)
        {
            proxyServers[i].Dispose();
        }

        if (ownsServer)
        {
            server.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
