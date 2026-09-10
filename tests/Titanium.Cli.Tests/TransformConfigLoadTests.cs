using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Cli.Tests;

[TestClass]
public class TransformConfigLoadTests
{
    [TestMethod]
    public void LoadJson_DeserializesTransforms()
    {
        var json = """
            {
              "schemaVersion": "7.0",
              "listeners": [ { "host": "127.0.0.1", "port": 1, "decryptSsl": false } ],
              "routes": [
                {
                  "id": "r1",
                  "clusterId": "c1",
                  "match": { "path": "/", "pathKind": "Prefix" },
                  "transforms": [
                    { "kind": "PathPrefix", "parameters": { "prefix": "/gw" } }
                  ]
                }
              ],
              "clusters": [
                {
                  "id": "c1",
                  "algorithm": "RoundRobin",
                  "destinations": [ { "id": "d1", "address": "127.0.0.1", "port": 2 } ]
                }
              ]
            }
            """;
        var c = TwpConfigLoader.LoadJson(json);
        Assert.AreEqual(1, c.Routes.Count);
        var transforms = c.Routes[0].Transforms;
        Assert.IsNotNull(transforms);
        Assert.AreEqual(1, transforms.Count);
        Assert.AreEqual("PathPrefix", transforms[0].Kind);
        Assert.AreEqual("/gw", transforms[0].Parameters!["prefix"]);
    }
}
