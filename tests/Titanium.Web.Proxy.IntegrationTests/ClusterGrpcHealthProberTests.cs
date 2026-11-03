using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class ClusterGrpcHealthProberTests
{
    [TestCleanup]
    public void Restore() => ClusterHealthProber.ProbeOverride = null;

    [TestMethod]
    [Timeout(60_000)]
    public async Task GrpcHealthProbe_SucceedsAgainstH2cOrigin()
    {
        using var origin = Http2RawOriginServer.CreateCleartext();
        origin.HandleConnection(ServeGrpcHealthAsync);
        // CI agents under load need more than a few seconds for cold HTTP/2 prior-knowledge + gRPC.
        using var client = new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        var ok = await GrpcHealthProbe.ProbeAsync(
            client,
            new Uri($"http://127.0.0.1:{origin.Port}{GrpcHealthProbe.CheckPath}"),
            null,
            null,
            CancellationToken.None);
        Assert.IsTrue(ok);
    }

    [TestMethod]
    public async Task GrpcProbe_MarksDestinationHealthyWhenOriginServes()
    {
        using var origin = Http2RawOriginServer.CreateCleartext();
        origin.HandleConnection(ServeGrpcHealthAsync);

        var port = origin.Port;
        var cluster = new ClusterConfig
        {
            Id = "c",
            Destinations = [new DestinationConfig { Id = "g", Address = "127.0.0.1", Port = port }],
            HealthCheck = new HealthCheckConfig
            {
                Active = new ActiveHealthConfig
                {
                    Enabled = true,
                    Kind = ActiveHealthKind.Grpc,
                    IntervalSeconds = 3600,
                    TimeoutSeconds = 5,
                    UnhealthyThreshold = 1,
                    HealthyThreshold = 1,
                },
            },
        };
        var manager = new ClusterManager();
        await manager.ApplyAsync([new ClusterConfig { Id = cluster.Id, Destinations = cluster.Destinations }]);
        using var prober = new ClusterHealthProber(manager, cluster, TimeProvider.System);
        ClusterHealthProber.ProbeOverride = (_, _) => Task.FromResult(false);
        await prober.ProbeOnceAsync();
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("g"));
        ClusterHealthProber.ProbeOverride = null;
        await prober.ProbeOnceAsync();
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("g"));
    }

    private static async Task ServeGrpcHealthAsync(Http2RawFrame.Connection conn)
    {
        await conn.SendInitialSettingsAsync();
        await conn.GetStream().FlushAsync();

        // ACK client SETTINGS while collecting the request in one pass. A separate "wait for SETTINGS
        // then ReadRequestAsync" phase can hang under load when WINDOW_UPDATE/PING/HEADERS arrive
        // interleaved and the SETTINGS wait loop consumes the wrong frame.
        var (streamId, _, _) = await ReadRequestAcknowledgingSettingsAsync(conn);
        var responseHeaders = conn.EncodeHeaders(
            [(":status", "200")],
            [("content-type", "application/grpc")]);
        await conn.WriteHeaderBlockAsync(streamId, responseHeaders, endStream: false);
        await conn.WriteFrameAsync(Http2FrameType.Data, streamId, 0, GrpcHealthProbe.EncodeResponse(GrpcHealthProbe.StatusServing));
        var trailers = conn.EncodeHeaders([], [("grpc-status", "0")]);
        await conn.WriteHeaderBlockAsync(streamId, trailers, endStream: true);
        await conn.GetStream().FlushAsync();
    }

    private static async Task<(int StreamId, List<(string Name, string Value)> Headers, byte[] Body)>
        ReadRequestAcknowledgingSettingsAsync(Http2RawFrame.Connection conn)
    {
        var streamId = -1;
        List<(string Name, string Value)>? requestHeaders = null;
        var body = new MemoryStream();

        while (true)
        {
            var frame = await conn.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Settings && frame.StreamId == 0 &&
                (frame.Flags & Http2FrameFlag.Ack) == 0)
            {
                await conn.WriteFrameAsync(Http2FrameType.Settings, 0, Http2FrameFlag.Ack, Array.Empty<byte>());
                continue;
            }

            if (frame.Type == Http2FrameType.Headers)
            {
                streamId = frame.StreamId;
                requestHeaders = conn.DecodeHeaders(frame.Payload);
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    break;
            }
            else if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
            {
                body.Write(frame.Payload, 0, frame.Payload.Length);
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    break;
            }
        }

        return (streamId, requestHeaders!, body.ToArray());
    }
}
