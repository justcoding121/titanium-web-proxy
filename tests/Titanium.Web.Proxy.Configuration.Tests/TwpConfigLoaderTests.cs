using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Parsers;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class TwpConfigLoaderTests
{
    [TestMethod]
    public void LoadJson_ParsesListenersAndRoutes()
    {
        var json = """
            {
              "listeners": [{ "host": "127.0.0.1", "port": 8080, "forwardHost": "example.com", "forwardPort": 80 }],
              "clusters": [{ "id": "c1", "destinations": [{ "id": "d1", "address": "example.com", "port": 80 }] }],
              "routes": [{ "id": "r1", "clusterId": "c1", "match": { "path": "/", "pathKind": "prefix" }, "order": 0 }]
            }
            """;
        var config = TwpConfigLoader.LoadJson(json);
        Assert.AreEqual(1, config.Listeners.Count);
        Assert.AreEqual(8080, config.Listeners[0].Port);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }

    [TestMethod]
    public void SiteFileReader_ParsesUpstream()
    {
        var config = SiteFileReader.Parse("api.example.com /api => http://127.0.0.1:5000\n");
        Assert.AreEqual(1, config.Routes.Count);
        Assert.AreEqual("api.example.com", config.Routes[0].Match.Host);
        Assert.AreEqual("127.0.0.1", config.Clusters[0].Destinations[0].Address);
        Assert.AreEqual(5000, config.Clusters[0].Destinations[0].Port);
    }

    [TestMethod]
    public void SiteFileReader_ParsesListenAndForward()
    {
        var config = SiteFileReader.Parse("""
            listen 127.0.0.1:8080
            forward 127.0.0.1:9000
            """);
        Assert.AreEqual(1, config.Listeners.Count);
        Assert.AreEqual("127.0.0.1", config.Listeners[0].Host);
        Assert.AreEqual(8080, config.Listeners[0].Port);
        Assert.AreEqual("127.0.0.1", config.Listeners[0].ForwardHost);
        Assert.AreEqual(9000, config.Listeners[0].ForwardPort);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }

    [TestMethod]
    public void HttpServerConfigReader_ParsesProxyPass()
    {
        var text = """
            server {
              listen 80;
              server_name www.example.com;
              location / {
                proxy_pass http://10.0.0.2:8080;
              }
            }
            """;
        var config = HttpServerConfigReader.Parse(text);
        Assert.AreEqual(1, config.Routes.Count);
        Assert.AreEqual("www.example.com", config.Routes[0].Match.Host);
        Assert.AreEqual("10.0.0.2", config.Clusters[0].Destinations[0].Address);
    }

    [TestMethod]
    public void JsonReverseProxyDocument_ParsesSubset()
    {
        var json = """
            {
              "listeners": [{ "address": "0.0.0.0", "port": 8000 }],
              "clusters": [{
                "id": "backend",
                "destinations": [{ "address": "192.168.1.10", "port": 80 }]
              }],
              "routes": [{
                "clusterId": "backend",
                "match": { "path": "/app", "pathKind": "prefix" }
              }]
            }
            """;
        var config = JsonReverseProxyDocument.Parse(json);
        Assert.AreEqual("backend", config.Routes[0].ClusterId);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }
}
