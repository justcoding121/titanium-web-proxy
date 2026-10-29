using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>Gateway API <c>Add</c>: an append extends existing values; it never replaces them.</summary>
[TestClass]
public class TransformHeaderAppendTests
{
    private static TransformConfig Config(string kind, string name, string? value = null)
    {
        var parameters = new Dictionary<string, string> { ["name"] = name };
        if (value is not null)
        {
            parameters["value"] = value;
        }

        return new TransformConfig { Kind = kind, Parameters = parameters };
    }

    private static TransformRequestContext Apply(
        Func<string, string?>? clientHeaders, params TransformConfig[] transforms)
    {
        var context = new TransformRequestContext { Path = "/", ExistingRequestHeader = clientHeaders };
        new TransformEngine().ApplyRequestTransforms(transforms, context);
        return context;
    }

    [TestMethod]
    public void RequestAppend_ExtendsTheValueTheClientSent_AndQueuesItsLinesForReplacement()
    {
        var context = Apply(
            name => name.Equals("X-Test", StringComparison.OrdinalIgnoreCase) ? "client" : null,
            Config(TransformKinds.RequestHeaderAppend, "X-Test", "added"));

        Assert.AreEqual("client, added", context.Headers["X-Test"]);
        // The dispatcher removes every client line first, then writes the merged value once.
        Assert.IsTrue(context.HeadersToRemove.Contains("X-Test"));
    }

    [TestMethod]
    public void RequestAppend_WithNoClientValue_AddsTheHeaderWithoutRemovingAnything()
    {
        var context = Apply(_ => null, Config(TransformKinds.RequestHeaderAppend, "X-Test", "added"));

        Assert.AreEqual("added", context.Headers["X-Test"]);
        Assert.AreEqual(0, context.HeadersToRemove.Count);
    }

    [TestMethod]
    public void RequestAppend_Repeated_ExtendsOneMergedValue()
    {
        var context = Apply(
            _ => "client",
            Config(TransformKinds.RequestHeaderAppend, "X-Test", "a"),
            Config(TransformKinds.RequestHeaderAppend, "X-Test", "b"));

        Assert.AreEqual("client, a, b", context.Headers["X-Test"]);
    }

    [TestMethod]
    public void RequestAppend_AfterSet_ExtendsTheStagedValue_NotTheClientValue()
    {
        var context = Apply(
            _ => "client",
            Config(TransformKinds.RequestHeaderSet, "X-Test", "set"),
            Config(TransformKinds.RequestHeaderAppend, "X-Test", "added"));

        Assert.AreEqual("set, added", context.Headers["X-Test"]);
    }

    [TestMethod]
    public void RequestAppend_AfterRemove_DoesNotResurrectTheClientValue()
    {
        var context = Apply(
            _ => "client",
            Config(TransformKinds.RequestHeaderRemove, "X-Test"),
            Config(TransformKinds.RequestHeaderAppend, "X-Test", "added"));

        Assert.AreEqual("added", context.Headers["X-Test"]);
    }

    [TestMethod]
    public void ResponseAppend_IsMarkedToExtendTheOriginValue()
    {
        var context = Apply(null, Config(TransformKinds.ResponseHeaderAppend, "X-R", "edge"));

        Assert.AreEqual("edge", context.ResponseHeadersToSet["X-R"]);
        Assert.IsTrue(context.ResponseHeadersToAppend!.Contains("x-r"));
    }

    [TestMethod]
    public void ResponseSet_IsNotMarkedAsAppend_AndOverridesAnEarlierAppend()
    {
        var set = Apply(null, Config(TransformKinds.ResponseHeaderSet, "X-R", "edge"));
        Assert.IsNull(set.ResponseHeadersToAppend);

        var overridden = Apply(
            null,
            Config(TransformKinds.ResponseHeaderAppend, "X-R", "a"),
            Config(TransformKinds.ResponseHeaderSet, "X-R", "b"));
        Assert.AreEqual("b", overridden.ResponseHeadersToSet["X-R"]);
        Assert.IsFalse(overridden.ResponseHeadersToAppend!.Contains("X-R"));
    }

    [TestMethod]
    public void ResponseAppend_Repeated_JoinsTheStagedValues_AndAfterSetStaysAReplacement()
    {
        var appends = Apply(
            null,
            Config(TransformKinds.ResponseHeaderAppend, "X-R", "a"),
            Config(TransformKinds.ResponseHeaderAppend, "X-R", "b"));
        Assert.AreEqual("a, b", appends.ResponseHeadersToSet["X-R"]);
        Assert.IsTrue(appends.ResponseHeadersToAppend!.Contains("X-R"));

        var afterSet = Apply(
            null,
            Config(TransformKinds.ResponseHeaderSet, "X-R", "set"),
            Config(TransformKinds.ResponseHeaderAppend, "X-R", "added"));
        Assert.AreEqual("set, added", afterSet.ResponseHeadersToSet["X-R"]);
        Assert.IsNull(afterSet.ResponseHeadersToAppend);
    }

    [TestMethod]
    public void ResponseRemove_AfterAppend_DropsTheStagedValue()
    {
        var context = Apply(
            null,
            Config(TransformKinds.ResponseHeaderAppend, "X-R", "a"),
            Config(TransformKinds.ResponseHeaderRemove, "X-R"));

        Assert.IsFalse(context.ResponseHeadersToSet.ContainsKey("X-R"));
        Assert.IsTrue(context.ResponseHeadersToRemove.Contains("X-R"));
    }

    [TestMethod]
    public void ResponsePlan_AppendExtendsOriginValues_SetReplacesThem()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("X-Append", "origin");
        headers.AddHeader("X-Multi", "one");
        headers.AddHeader("X-Multi", "two");
        headers.AddHeader("X-Set", "origin");
        headers.AddHeader("Set-Cookie", "a=1");

        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Append"] = "edge",
            ["X-Multi"] = "edge",
            ["X-Absent"] = "edge",
            ["X-Set"] = "edge",
            ["Set-Cookie"] = "b=2",
        };
        var append = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "X-Append", "X-Multi", "X-Absent", "Set-Cookie" };

        new TransformResponseHeaderPlan(set, new HashSet<string>(), append).Apply(headers);

        Assert.AreEqual("origin, edge", headers.GetFirstHeader("X-Append")!.Value);
        Assert.AreEqual(1, headers.GetHeaders("X-Multi")!.Count);
        Assert.AreEqual("one, two, edge", headers.GetFirstHeader("X-Multi")!.Value);
        Assert.AreEqual("edge", headers.GetFirstHeader("X-Absent")!.Value);
        Assert.AreEqual("edge", headers.GetFirstHeader("X-Set")!.Value);

        var cookies = headers.GetHeaders("Set-Cookie")!;
        Assert.AreEqual(2, cookies.Count);
        Assert.AreEqual("a=1", cookies[0].Value);
        Assert.AreEqual("b=2", cookies[1].Value);
    }

    [TestMethod]
    public void JoinHeaderValues_CoversAbsentSingleAndRepeatedLines()
    {
        Assert.IsNull(ReverseProxySessionDispatch.JoinHeaderValues(null));
        Assert.IsNull(ReverseProxySessionDispatch.JoinHeaderValues([]));

        var headers = new HeaderCollection();
        headers.AddHeader("X-One", "a");
        headers.AddHeader("X-Two", "a");
        headers.AddHeader("X-Two", "b");
        Assert.AreEqual("a", ReverseProxySessionDispatch.JoinHeaderValues(headers.GetHeaders("X-One")));
        Assert.AreEqual("a, b", ReverseProxySessionDispatch.JoinHeaderValues(headers.GetHeaders("X-Two")));
    }
}
