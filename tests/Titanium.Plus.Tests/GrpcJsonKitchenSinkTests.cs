using System.Net;
using System.Reflection;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Grpc;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;
using Label = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;

namespace Titanium.Plus.Tests;

[TestClass]
public class GrpcJsonKitchenSinkTests
{
    private static string FixturePb =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "GrpcTranscode", "greeter.pb");

    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    [TestMethod]
    public void ProtoJson_AndDescriptorMessage_CoverScalarRepeatedNestedAndEnum()
    {
        var sinkType = BuildSinkDescriptor();
        var json = """
            {"s":"hi","flag":true,"blob":"YWI=","i32":"12","u32":3,"i64":"4","u64":5,
             "f32":"1.5","f64":2.5,"sf32":-1,"sf64":-2,"fx32":7,"fx64":8,"sfx32":9,"sfx64":10,
             "color":"RED","inner":{"n":"x"},"tags":["a","b"],"unknown":1,"empty":null}
            """;
        var msg = ProtoJson.Parse(json, sinkType, ignoreUnknown: true);
        var formatted = ProtoJson.Format(msg, preserveProtoFieldNames: true, alwaysPrintPrimitiveFields: true);
        StringAssert.Contains(formatted, "hi");
        StringAssert.Contains(formatted, "\"color\":1");

        _ = ProtoJson.Format(new DescriptorMessage(sinkType), preserveProtoFieldNames: false, alwaysPrintPrimitiveFields: true);
        _ = ProtoJson.Parse("{\"i32\":9,\"u32\":\"3\",\"i64\":4,\"u64\":\"5\",\"f32\":1.25,\"f64\":\"2.5\",\"color\":1}", sinkType, true);
        _ = ProtoJson.Parse("{\"s\":null,\"tags\":[]}", sinkType, true);

        var bytes = msg.ToByteArray();
        Assert.IsTrue(bytes.Length > 0);
        Assert.IsTrue(msg.CalculateSize() > 0);
        var again = new DescriptorMessage(sinkType);
        again.MergeFrom(bytes);
        Assert.AreEqual("hi", again.Values[1]);

        again.MergeFrom([(byte)((15 << 3) | 0), 1]);
        again.Values[2] = null;
        _ = ProtoJson.Format(again, false, false);

        Assert.ThrowsExactly<InvalidOperationException>(() => ProtoJson.Parse("{\"color\":\"NOPE\"}", sinkType, true));
        Assert.ThrowsExactly<InvalidOperationException>(() => ProtoJson.Parse("{\"inner\":[]}", sinkType, true));
        Assert.ThrowsExactly<InvalidOperationException>(() => ProtoJson.Parse("{\"tags\":\"x\"}", sinkType, true));
    }

    [TestMethod]
    public void LoadFromDescriptorSet_RequiresServices_AndGzipFlag()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            GrpcJsonTranscoderImpl.LoadFromDescriptorSet(FixturePb, [], true, true, false, false));
        var gzip = GrpcJsonTranscoderImpl.LoadFromDescriptorSet(
            FixturePb, ["helloworld.Greeter"], true, true, false, false, enableGzipCompression: true);
        Assert.IsNotNull(gzip);
    }

    [TestMethod]
    public async Task TryRewrite_CoversGetPostGzipQueryAndResponseFrames()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        using var session = MakeSession(proxy);

        var plain = GrpcJsonTranscoderImpl.LoadFromDescriptorSet(
            FixturePb, ["helloworld.Greeter"], true, true, false, true);
        Assert.IsFalse(await plain.TryRewriteRequestAsync("not-a-session"));
        Assert.IsFalse(await plain.TryRewriteResponseAsync("not-a-session"));

        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "/v1/greeter/Ada?unknown=1";
        Assert.IsTrue(await plain.TryRewriteRequestAsync(session));

        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "https://example.test/v1/greeter/Bob";
        Assert.IsTrue(await plain.TryRewriteRequestAsync(session));

        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "v1/greeter/Cara";
        Assert.IsTrue(await plain.TryRewriteRequestAsync(session));

        session.HttpClient.Request.Method = "POST";
        session.HttpClient.Request.RequestUriString = "/v1/greeter";
        session.SetRequestBody("{ \"name\": \"Dee\" }"u8.ToArray());
        Assert.IsTrue(await plain.TryRewriteRequestAsync(session));

        var strict = GrpcJsonTranscoderImpl.LoadFromDescriptorSet(
            FixturePb, ["helloworld.Greeter"], true, ignoreUnknownQueryParameters: false, false, false);
        var bytes = File.ReadAllBytes(FixturePb);
        var set = FileDescriptorSet.Parser.ParseFrom(bytes);
        var files = FileDescriptor.BuildFromByteStrings(set.File.Select(f => f.ToByteString()).ToList());
        var greeter = files.First(f => f.Name.EndsWith("greeter.proto", StringComparison.Ordinal));
        var input = greeter.MessageTypes.First(m => m.Name == "HelloRequest");
        var apply = typeof(GrpcJsonTranscoderImpl).GetMethod("ApplyQueryParameters", PrivateStatic | BindingFlags.Instance)!;
        var queryEx = Assert.ThrowsExactly<TargetInvocationException>(() =>
            apply.Invoke(strict, [new DescriptorMessage(input), "/v1/greeter/Ada?nope=1",
                new Dictionary<string, string>(StringComparer.Ordinal)]));
        Assert.IsInstanceOfType<InvalidOperationException>(queryEx.InnerException);

        var gzip = GrpcJsonTranscoderImpl.LoadFromDescriptorSet(
            FixturePb, ["helloworld.Greeter"], true, true, false, false, enableGzipCompression: true);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "/v1/greeter/Ada";
        Assert.IsTrue(await gzip.TryRewriteRequestAsync(session));

        session.HttpClient.Request.Method = "PUT";
        session.HttpClient.Request.RequestUriString = "/v1/greeter/Ada";
        Assert.IsFalse(await plain.TryRewriteRequestAsync(session));

        await CoverResponseRewriteAsync(plain, session);
        CoverPrivateHelpers(plain, gzip);
    }

    private static async Task CoverResponseRewriteAsync(GrpcJsonTranscoderImpl transcoder, SessionEventArgs session)
    {
        var bytes = File.ReadAllBytes(FixturePb);
        var set = FileDescriptorSet.Parser.ParseFrom(bytes);
        var files = FileDescriptor.BuildFromByteStrings(set.File.Select(f => f.ToByteString()).ToList());
        var greeter = files.First(f => f.Name.EndsWith("greeter.proto", StringComparison.Ordinal));
        var reply = greeter.MessageTypes.First(m => m.Name == "HelloReply");
        var msg = ProtoJson.Parse("{\"message\":\"Hello Ada\"}", reply, true);
        var framed = GrpcFrames.Encode(msg.ToByteArray());
        var gzipBytes = (byte[])typeof(GrpcJsonTranscoderImpl)
            .GetMethod("Gzip", PrivateStatic)!.Invoke(null, [msg.ToByteArray()])!;
        var gzipFramed = GrpcFrames.Encode(gzipBytes, compressed: true);
        var twoFrames = framed.Concat(framed).ToArray();

        session.HttpClient.Request.Locked = true;
        session.HttpClient.Response.IsBodyRead = true;
        session.HttpClient.Response.Body = framed;
        session.HttpClient.Response.Headers.AddHeader("grpc-status", "0");
        session.UserData = new GrpcJsonTranscodeSessionMark
        {
            ClientMethod = "GET",
            ClientPathAndQuery = "/v1/greeter/Ada",
            UpstreamMethod = "POST",
            UpstreamPath = "/helloworld.Greeter/SayHello",
            OutputMessageType = reply.FullName,
        };
        Assert.IsTrue(await transcoder.TryRewriteResponseAsync(session));

        session.HttpClient.Request.Locked = true;
        session.HttpClient.Response.Locked = false;
        session.HttpClient.Response.IsBodyRead = true;
        session.HttpClient.Response.Body = gzipFramed;
        session.HttpClient.Response.Headers.RemoveHeader("grpc-status");
        session.HttpClient.Response.Headers.AddHeader("grpc-status", "0");
        Assert.IsTrue(await transcoder.TryRewriteResponseAsync(session));

        session.HttpClient.Request.Locked = true;
        session.HttpClient.Response.Locked = false;
        session.HttpClient.Response.IsBodyRead = true;
        session.HttpClient.Response.Body = twoFrames;
        Assert.IsTrue(await transcoder.TryRewriteResponseAsync(session));

        foreach (var code in new[] { "2", "3", "4", "5", "8", "12", "13", "14", "16", "99" })
        {
            session.HttpClient.Request.Locked = true;
            session.HttpClient.Response.Locked = false;
            session.HttpClient.Response.IsBodyRead = true;
            session.HttpClient.Response.Body = Array.Empty<byte>();
            session.HttpClient.Response.Headers.RemoveHeader("grpc-status");
            session.HttpClient.Response.Headers.AddHeader("grpc-status", code);
            session.HttpClient.Response.Headers.RemoveHeader("grpc-message");
            session.HttpClient.Response.Headers.AddHeader("grpc-message", "err");
            Assert.IsTrue(await transcoder.TryRewriteResponseAsync(session));
        }

        var noConvert = GrpcJsonTranscoderImpl.LoadFromDescriptorSet(
            FixturePb, ["helloworld.Greeter"], convertGrpcStatus: false, true, false, false);
        session.HttpClient.Request.Locked = true;
        session.HttpClient.Response.Locked = false;
        session.HttpClient.Response.IsBodyRead = true;
        session.HttpClient.Response.Body = Array.Empty<byte>();
        session.HttpClient.Response.Headers.RemoveHeader("grpc-status");
        session.HttpClient.Response.Headers.AddHeader("grpc-status", "5");
        Assert.IsTrue(await noConvert.TryRewriteResponseAsync(session));

        session.UserData = null;
        Assert.IsFalse(await transcoder.TryRewriteResponseAsync(session));
    }

    private static void CoverPrivateHelpers(GrpcJsonTranscoderImpl plain, GrpcJsonTranscoderImpl gzip)
    {
        var gzipFn = typeof(GrpcJsonTranscoderImpl).GetMethod("Gzip", PrivateStatic)!;
        var gunzipFn = typeof(GrpcJsonTranscoderImpl).GetMethod("Gunzip", PrivateStatic)!;
        var payload = "hello-transcode"u8.ToArray();
        var compressed = (byte[])gzipFn.Invoke(null, [payload])!;
        CollectionAssert.AreEqual(payload, (byte[])gunzipFn.Invoke(null, [compressed])!);

        var pathFn = typeof(GrpcJsonTranscoderImpl).GetMethod("GetPathAndQuery", PrivateStatic)!;
        var empty = new Request();
        Assert.AreEqual("/", pathFn.Invoke(null, [empty]));
        empty.RequestUriString = "relative";
        Assert.AreEqual("/relative", pathFn.Invoke(null, [empty]));
        empty.RequestUriString = "/already";
        Assert.AreEqual("/already", pathFn.Invoke(null, [empty]));
        empty.RequestUriString = "https://h.test/x?q=1";
        Assert.AreEqual("/x?q=1", pathFn.Invoke(null, [empty]));

        var statusFn = typeof(GrpcJsonTranscoderImpl).GetMethod("HttpStatusDescription", PrivateStatic)!;
        foreach (var code in new[] { 200, 400, 401, 403, 404, 409, 429, 499, 501, 503, 504, 418 })
            Assert.IsFalse(string.IsNullOrEmpty((string)statusFn.Invoke(null, [code])!));

        var framesFn = typeof(GrpcJsonTranscoderImpl).GetMethod("TryReadAllFrames", PrivateStatic)!;
        var args = new object?[] { Array.Empty<byte>(), null };
        Assert.IsFalse((bool)framesFn.Invoke(null, args)!);
        args = [new byte[] { 0, 0, 0, 0, 20 }, null];
        _ = framesFn.Invoke(null, args);

        var sink = BuildSinkDescriptor();
        var message = new DescriptorMessage(sink);
        var setFn = typeof(GrpcJsonTranscoderImpl).GetMethod("SetScalarField", PrivateStatic)!;
        setFn.Invoke(null, [message, "missing", "x"]);
        setFn.Invoke(null, [message, "s", "hi"]);
        setFn.Invoke(null, [message, "flag", "true"]);
        setFn.Invoke(null, [message, "i32", "1"]);
        setFn.Invoke(null, [message, "u32", "2"]);
        setFn.Invoke(null, [message, "i64", "3"]);
        setFn.Invoke(null, [message, "u64", "4"]);
        setFn.Invoke(null, [message, "f32", "1.5"]);
        setFn.Invoke(null, [message, "f64", "2.5"]);
        setFn.Invoke(null, [message, "blob", Convert.ToBase64String("ab"u8.ToArray())]);
        setFn.Invoke(null, [message, "color", "1"]);
        _ = gzip;
        _ = Encoding.UTF8.GetByteCount(ProtoJson.Format(message, false, true));
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

    private static MessageDescriptor BuildSinkDescriptor()
    {
        static FieldDescriptorProto Fd(string name, int number, Type type, string? typeName = null, bool repeated = false)
        {
            var field = new FieldDescriptorProto
            {
                Name = name,
                JsonName = name,
                Number = number,
                Type = type,
                Label = repeated ? Label.Repeated : Label.Optional,
            };
            if (typeName is not null)
                field.TypeName = typeName;
            return field;
        }

        var inner = new DescriptorProto
        {
            Name = "Inner",
            Field = { Fd("n", 1, Type.String) },
        };
        var sink = new DescriptorProto
        {
            Name = "Sink",
            Field =
            {
                Fd("s", 1, Type.String),
                Fd("flag", 2, Type.Bool),
                Fd("blob", 3, Type.Bytes),
                Fd("i32", 4, Type.Int32),
                Fd("u32", 5, Type.Uint32),
                Fd("i64", 6, Type.Int64),
                Fd("u64", 7, Type.Uint64),
                Fd("f32", 8, Type.Float),
                Fd("f64", 9, Type.Double),
                Fd("sf32", 10, Type.Sint32),
                Fd("sf64", 11, Type.Sint64),
                Fd("fx32", 12, Type.Fixed32),
                Fd("fx64", 13, Type.Fixed64),
                Fd("sfx32", 14, Type.Sfixed32),
                Fd("sfx64", 15, Type.Sfixed64),
                Fd("color", 16, Type.Enum, ".cov.Color"),
                Fd("inner", 17, Type.Message, ".cov.Inner"),
                Fd("tags", 18, Type.String, repeated: true),
            },
        };
        var file = new FileDescriptorProto
        {
            Name = "sink.proto",
            Package = "cov",
            Syntax = "proto3",
            EnumType =
            {
                new EnumDescriptorProto
                {
                    Name = "Color",
                    Value =
                    {
                        new EnumValueDescriptorProto { Name = "COLOR_UNSPECIFIED", Number = 0 },
                        new EnumValueDescriptorProto { Name = "RED", Number = 1 },
                    },
                },
            },
            MessageType = { inner, sink },
        };
        var set = new FileDescriptorSet { File = { file } };
        var files = FileDescriptor.BuildFromByteStrings(set.File.Select(f => f.ToByteString()).ToList());
        return files[0].MessageTypes.First(m => m.Name == "Sink");
    }
}
