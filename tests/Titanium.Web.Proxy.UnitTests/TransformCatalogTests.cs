using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class TransformCatalogTests
{
    [TestMethod]
    public void PathSet_ReplacesThePath()
    {
        var ctx = Apply(TransformKinds.PathSet, new Dictionary<string, string> { ["path"] = "/health" }, "/old");
        Assert.AreEqual("/health", ctx.Path);
    }

    [TestMethod]
    public void PathRegexReplace_Rewrites()
    {
        var ctx = Apply(TransformKinds.PathRegexReplace, new Dictionary<string, string>
        {
            ["pattern"] = "^/v1",
            ["replacement"] = "/v2",
        }, "/v1/items");
        Assert.AreEqual("/v2/items", ctx.Path);
    }

    [TestMethod]
    public void HttpMethodChange_ChangesGet()
    {
        var ctx = new TransformRequestContext { Path = "/", Method = "GET" };
        new TransformEngine().ApplyRequestTransforms(
        [
            new TransformConfig
            {
                Kind = TransformKinds.HttpMethodChange,
                Parameters = new Dictionary<string, string> { ["from"] = "GET", ["to"] = "POST" },
            },
        ], ctx);
        Assert.AreEqual("POST", ctx.Method);
    }

    [TestMethod]
    public void QueryRemove_DropsTheNamedParameter()
    {
        var ctx = Apply(TransformKinds.QueryRemove, new Dictionary<string, string> { ["name"] = "b" }, "/x?a=1&b=2");
        Assert.AreEqual("/x?a=1", ctx.Path);
    }

    [TestMethod]
    public void RequestHeaderAppend_JoinsValues()
    {
        var ctx = new TransformRequestContext { Path = "/" };
        ctx.Headers["Accept"] = "text/plain";
        new TransformEngine().ApplyRequestTransforms(
        [
            new TransformConfig
            {
                Kind = TransformKinds.RequestHeaderAppend,
                Parameters = new Dictionary<string, string> { ["name"] = "Accept", ["value"] = "application/json" },
            },
        ], ctx);
        Assert.AreEqual("text/plain, application/json", ctx.Headers["Accept"]);
    }

    private static TransformRequestContext Apply(string kind, Dictionary<string, string> parameters, string path)
    {
        var ctx = new TransformRequestContext { Path = path };
        new TransformEngine().ApplyRequestTransforms(
        [
            new TransformConfig { Kind = kind, Parameters = parameters },
        ], ctx);
        return ctx;
    }
}
