using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http3ProxyAuthorizationTests
{
    [TestMethod]
    public async Task MissingCredentials_Returns407WithoutProxyConnection()
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(false);
        var session = MakeSession(proxy);

        var allowed = await proxy.CheckHttp3AuthorizationAsync(session);

        Assert.IsFalse(allowed);
        var response = session.HttpClient.Response;
        Assert.AreEqual(407, response.StatusCode);
        Assert.AreEqual(HttpHeader.Version30, response.HttpVersion);
        Assert.IsNull(response.Headers.GetFirstHeader("proxy-connection"));
        StringAssert.StartsWith(response.Headers.GetFirstHeader("proxy-authenticate")!.Value, "Basic ");
    }

    [TestMethod]
    public async Task ValidBasicCredentials_AllowTheRequest()
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.ProxyBasicAuthenticateFunc = (_, user, password) =>
            Task.FromResult(user == "ada" && password == "secret");
        var session = MakeSession(proxy);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("ada:secret"));
        session.HttpClient.Request.Headers.AddHeader("proxy-authorization", "Basic " + token);

        var allowed = await proxy.CheckHttp3AuthorizationAsync(session);

        Assert.IsTrue(allowed);
        Assert.IsFalse(session.HttpClient.HasResponse);
    }

    [TestMethod]
    public void AuthenticationUnset_KeepsTheFastPathEligible()
    {
        var proxy = new ProxyServer(false, false, false);
        Assert.IsFalse(proxy.ProxyAuthenticationRequired);
        proxy.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(true);
        Assert.IsTrue(proxy.ProxyAuthenticationRequired);
    }

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }
}
