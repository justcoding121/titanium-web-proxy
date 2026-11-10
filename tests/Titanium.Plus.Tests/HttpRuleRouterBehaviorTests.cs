using System.Reflection;
using Google.Api;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Grpc;

namespace Titanium.Plus.Tests;

/// <summary>
///     HttpRuleRouter.AddRule method/pattern arms beyond the Greeter GET fixture.
/// </summary>
[TestClass]
public class HttpRuleRouterBehaviorTests
{
    private static string FixturePb =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "GrpcTranscode", "greeter.pb");

    [TestMethod]
    public void AddRule_RegistersPutPatchDeleteCustom_AndSkipsEmptyPattern()
    {
        var bytes = File.ReadAllBytes(FixturePb);
        var set = FileDescriptorSet.Parser.ParseFrom(bytes);
        var files = FileDescriptor.BuildFromByteStrings(set.File.Select(f => f.ToByteString()).ToList());
        var method = files.SelectMany(f => f.Services).SelectMany(s => s.Methods).First();

        var routes = new List<TranscodedRoute>();
        Add(routes, method, new HttpRule { Put = "/v1/put/{name}" });
        Add(routes, method, new HttpRule { Patch = "/v1/patch/{name}" });
        Add(routes, method, new HttpRule { Delete = "/v1/del/{name}" });
        Add(routes, method, new HttpRule { Post = "/v1/post/{name}" });
        Add(routes, method, new HttpRule
        {
            Custom = new CustomHttpPattern { Kind = "HEAD", Path = "/v1/head/{name}" },
        });
        Add(routes, method, new HttpRule()); // no pattern
        Add(routes, method, new HttpRule { Get = "   " }); // blank pattern

        Assert.AreEqual(5, routes.Count);
        var router = CreateRouter(routes);
        Assert.IsTrue(router.TryMatch("PUT", "/v1/put/Ada", out _, out var putVars));
        Assert.AreEqual("Ada", putVars["name"]);
        Assert.IsTrue(router.TryMatch("PATCH", "/v1/patch/Ada", out _, out _));
        Assert.IsTrue(router.TryMatch("DELETE", "/v1/del/Ada", out _, out _));
        Assert.IsTrue(router.TryMatch("POST", "/v1/post/Ada", out _, out _));
        Assert.IsTrue(router.TryMatch("HEAD", "/v1/head/Ada", out _, out _));
    }

    private static void Add(List<TranscodedRoute> routes, MethodDescriptor method, HttpRule rule) =>
        typeof(HttpRuleRouter)
            .GetMethod("AddRule", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [routes, method, rule]);

    private static HttpRuleRouter CreateRouter(List<TranscodedRoute> routes) =>
        (HttpRuleRouter)typeof(HttpRuleRouter)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0]
            .Invoke([routes]);
}
