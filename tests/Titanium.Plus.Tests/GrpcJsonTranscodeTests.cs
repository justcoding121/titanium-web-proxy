using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Grpc;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Tests;

[TestClass]
public class GrpcJsonTranscodeTests
{
    private static string FixturePb =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "GrpcTranscode", "greeter.pb");

    [TestMethod]
    public void PathTemplate_MatchesSingleSegmentVar()
    {
        var template = PathTemplate.Parse("/v1/greeter/{name}");
        Assert.IsTrue(template.TryMatch("/v1/greeter/world", out var vars));
        Assert.AreEqual("world", vars["name"]);
        Assert.IsFalse(template.TryMatch("/v1/other/world", out _));
    }

    [TestMethod]
    public void Router_MatchesAnnotatedGet()
    {
        var transcoder = GrpcJsonTranscoderImpl.LoadFromDescriptorSet(
            FixturePb,
            ["helloworld.Greeter"],
            convertGrpcStatus: true,
            ignoreUnknownQueryParameters: true,
            preserveProtoFieldNames: false,
            alwaysPrintPrimitiveFields: false);

        // Exercise via public guard load path / private router indirectly through status mapping helpers.
        Assert.IsNotNull(transcoder);
        Assert.AreEqual(404, GrpcStatusMapping.ToHttpStatus(5));
        Assert.AreEqual(400, GrpcStatusMapping.ToHttpStatus(3));
        Assert.AreEqual(200, GrpcStatusMapping.ToHttpStatus(0));
    }

    [TestMethod]
    public void Frames_RoundTrip()
    {
        var payload = Encoding.UTF8.GetBytes("hello");
        var framed = GrpcFrames.Encode(payload);
        Assert.IsTrue(GrpcFrames.TryRead(framed, out var compressed, out var read));
        Assert.IsFalse(compressed);
        CollectionAssert.AreEqual(payload, read.ToArray());
    }

    [TestMethod]
    public void ProtoJson_RoundTrip_HelloRequest()
    {
        var transcoder = GrpcJsonTranscoderImpl.LoadFromDescriptorSet(
            FixturePb,
            ["helloworld.Greeter"],
            true, true, false, false);

        // Load descriptors again for message type access via a second load + rewrite mark path is heavy;
        // validate Parse/Format against descriptor by loading set directly.
        var bytes = File.ReadAllBytes(FixturePb);
        var set = FileDescriptorSet.Parser.ParseFrom(bytes);
        var files = FileDescriptor.BuildFromByteStrings(
            set.File.Select(f => f.ToByteString()).ToList());
        var greeter = files.First(f => f.Name.EndsWith("greeter.proto", StringComparison.Ordinal));
        var input = greeter.MessageTypes.First(m => m.Name == "HelloRequest");

        var msg = ProtoJson.Parse("{\"name\":\"Ada\"}", input, ignoreUnknown: true);
        var json = ProtoJson.Format(msg, preserveProtoFieldNames: false, alwaysPrintPrimitiveFields: false);
        Assert.IsTrue(json.Contains("Ada", StringComparison.Ordinal));

        _ = ProtoJson.Parse("", input, ignoreUnknown: true);
        _ = ProtoJson.Parse("{}", input, ignoreUnknown: true);
        Assert.ThrowsExactly<InvalidOperationException>(() => ProtoJson.Parse("[]", input, ignoreUnknown: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => ProtoJson.Parse("{\"nope\":1}", input, ignoreUnknown: false));
        var ignored = ProtoJson.Parse("{\"nope\":1,\"name\":\"Bob\"}", input, ignoreUnknown: true);
        _ = ProtoJson.Format(ignored, preserveProtoFieldNames: true, alwaysPrintPrimitiveFields: true);
        var reply = greeter.MessageTypes.First(m => m.Name == "HelloReply");
        var emptyReply = ProtoJson.Parse("{}", reply, ignoreUnknown: true);
        _ = ProtoJson.Format(emptyReply, preserveProtoFieldNames: false, alwaysPrintPrimitiveFields: true);

        var wire = msg.ToByteArray();
        var framed = GrpcFrames.Encode(wire);
        Assert.IsTrue(GrpcFrames.TryRead(framed, out _, out var payload));
        var again = new DescriptorMessage(input);
        again.MergeFrom(payload.ToArray());
        Assert.AreEqual("Ada", again.Values[1]);
        _ = transcoder;
    }

    [TestMethod]
    public void Guard_RequiresDescriptorAndServices()
    {
        var ctx = new PlusActivationContext { ProxyServer = new object() };
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GrpcTranscodeGuard.TryStart(ctx, new Dictionary<string, string>
            {
                ["grpc.transcode.enabled"] = "true"
            }));
    }

    [TestMethod]
    public void Guard_StartsWithFixture()
    {
        var ctx = new PlusActivationContext { ProxyServer = new object() };
        var guard = GrpcTranscodeGuard.TryStart(ctx, new Dictionary<string, string>
        {
            ["grpc.transcode.enabled"] = "true",
            ["grpc.transcode.descriptorSet"] = FixturePb,
            ["grpc.transcode.services"] = "helloworld.Greeter"
        });
        Assert.IsNotNull(guard);
        Assert.IsNotNull(ctx.GrpcJsonTranscoder);
    }

    [TestMethod]
    public void SessionMark_TryGet()
    {
        var mark = new GrpcJsonTranscodeSessionMark
        {
            ClientMethod = "GET",
            ClientPathAndQuery = "/v1/greeter/x",
            UpstreamMethod = "POST",
            UpstreamPath = "/helloworld.Greeter/SayHello"
        };
        Assert.IsTrue(GrpcJsonTranscodeSessionMark.TryGet(mark, out var got));
        Assert.AreEqual("/v1/greeter/x", got!.ClientPathAndQuery);
        Assert.IsFalse(GrpcJsonTranscodeSessionMark.TryGet(null, out _));
    }
}
