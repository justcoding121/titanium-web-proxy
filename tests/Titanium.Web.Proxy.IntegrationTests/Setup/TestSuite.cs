using System;
using System.Collections.Generic;
using System.Net.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

public class TestSuite : IDisposable
{
    private readonly TestServer server;
    private readonly List<ProxyServer> proxyServers = new();
    private bool disposed;

    public TestSuite(bool requireMutualTls = false)
    {
        using var dummyProxy = new ProxyServer();
        var serverCertificate = dummyProxy.CertificateManager.CreateServerCertificate("localhost").Result;
        server = new TestServer(serverCertificate, requireMutualTls);
    }

    public TestServer GetServer()
    {
        return server;
    }

    public ProxyServer GetProxy(ProxyServer upStreamProxy = null)
    {
        var proxyServer = new TestProxyServer(false, upStreamProxy).ProxyServer;
        proxyServers.Add(proxyServer);
        return proxyServer;
    }

    public ProxyServer GetReverseProxy(ProxyServer upStreamProxy = null)
    {
        var proxyServer = new TestProxyServer(true, upStreamProxy).ProxyServer;
        proxyServers.Add(proxyServer);
        return proxyServer;
    }

    public HttpClient GetClient(ProxyServer proxyServer, bool enableBasicProxyAuthorization = false)
    {
        return TestHelper.GetHttpClient(proxyServer.ProxyEndPoints[0].Port, enableBasicProxyAuthorization);
    }

    public HttpClient GetReverseProxyClient()
    {
        return TestHelper.GetHttpClient();
    }

    public void Dispose()
    {
        if (disposed) return;

        disposed = true;

        for (var i = proxyServers.Count - 1; i >= 0; i--)
        {
            proxyServers[i].Dispose();
        }

        server.Dispose();
    }
}
