using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class TransformEngineTests
{
    [TestMethod]
    public void PathPrefix_And_QueryValueSet_And_HeaderOps()
    {
        var engine = new TransformEngine();
        var ctx = new TransformRequestContext { Path = "/api/v1" };
        ctx.Headers["X-Remove-Me"] = "1";
        engine.ApplyRequestTransforms(
        [
            new TransformConfig { Kind = "PathPrefix", Parameters = new Dictionary<string, string> { ["prefix"] = "/gateway" } },
            new TransformConfig { Kind = "QueryValueSet", Parameters = new Dictionary<string, string> { ["name"] = "env", ["value"] = "lab" } },
            new TransformConfig { Kind = "RequestHeaderSet", Parameters = new Dictionary<string, string> { ["name"] = "X-Added", ["value"] = "yes" } },
            new TransformConfig { Kind = "RequestHeaderRemove", Parameters = new Dictionary<string, string> { ["name"] = "X-Remove-Me" } },
            new TransformConfig { Kind = "ResponseHeaderSet", Parameters = new Dictionary<string, string> { ["name"] = "X-Resp", ["value"] = "1" } },
            new TransformConfig { Kind = "ResponseHeaderRemove", Parameters = new Dictionary<string, string> { ["name"] = "Server" } },
        ], ctx);

        Assert.AreEqual("/gateway/api/v1?env=lab", ctx.Path);
        Assert.AreEqual("yes", ctx.Headers["X-Added"]);
        Assert.IsTrue(ctx.HeadersToRemove.Contains("X-Remove-Me"));
        Assert.AreEqual("1", ctx.ResponseHeadersToSet["X-Resp"]);
        Assert.IsTrue(ctx.ResponseHeadersToRemove.Contains("Server"));
    }

    [TestMethod]
    public void EmptyTransforms_NoOp()
    {
        var engine = new TransformEngine();
        var ctx = new TransformRequestContext { Path = "/x" };
        engine.ApplyRequestTransforms(null, ctx);
        engine.ApplyRequestTransforms([], ctx);
        Assert.AreEqual("/x", ctx.Path);
    }

    [TestMethod]
    public void PathRemovePrefix_And_QueryReplace_And_PrefixWithoutSlash()
    {
        var engine = new TransformEngine();
        var ctx = new TransformRequestContext { Path = "/gateway/api/v1?env=prod" };
        engine.ApplyRequestTransforms(
        [
            new TransformConfig { Kind = "PathRemovePrefix", Parameters = new Dictionary<string, string> { ["prefix"] = "/gateway" } },
            new TransformConfig { Kind = "PathPrefix", Parameters = new Dictionary<string, string> { ["prefix"] = "edge" } },
            new TransformConfig { Kind = "QueryValueSet", Parameters = new Dictionary<string, string> { ["name"] = "env", ["value"] = "lab" } },
            new TransformConfig { Kind = "PathPrefix", Parameters = new Dictionary<string, string> { ["prefix"] = "" } },
            new TransformConfig { Kind = "UnknownKind" },
        ], ctx);
        Assert.AreEqual("/edge/api/v1?env=lab", ctx.Path);

        var bare = new TransformRequestContext { Path = "no-slash" };
        engine.ApplyRequestTransforms(
        [
            new TransformConfig { Kind = "PathRemovePrefix", Parameters = new Dictionary<string, string> { ["prefix"] = "no" } },
        ], bare);
        Assert.IsTrue(bare.Path.StartsWith('/'));
    }
}
