using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     A client that cancels a stream (RST_STREAM) while the origin is still working: the origin's response
///     HEADERS then arrive for a stream the proxy has already removed. They must not be turned into a brand
///     new session with an empty request (<c>http://</c>, HTTP version 0.0) that reaches user
///     <c>BeforeResponse</c> handlers. The Inspector showed those as a nameless row with a <c>?</c> client
///     protocol.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2LateOriginResponseTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ResponseForStreamResetByClient_DoesNotReachBeforeResponseAsEmptySession()
    {
        var originGotRequest = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            originGotRequest.TrySetResult(streamId);

            // Answer only after the client has cancelled the stream and the proxy has dropped it.
            await releaseResponse.Task;
            var block = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, block, endStream: true);
            responseSent.TrySetResult();
            await Task.Delay(500);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var seen = new ConcurrentQueue<(string? Method, string Url, Version Version)>();
        proxy.BeforeRequest += (_, _) => Task.CompletedTask;
        proxy.BeforeResponse += (_, e) =>
        {
            var request = e.HttpClient.Request;
            seen.Enqueue((request.Method, request.Url ?? string.Empty, request.HttpVersion));
            return Task.CompletedTask;
        };

        proxy.AfterResponse += (_, e) =>
        {
            var request = e.HttpClient.Request;
            seen.Enqueue((request.Method, request.Url ?? string.Empty, request.HttpVersion));
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);
        var requestBlock = raw.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", $"localhost:{origin.Port}"), (":path", "/slow") },
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, requestBlock, endStream: true);

        await originGotRequest.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var cancel = new byte[4];
        cancel[3] = (byte)Http2ErrorCode.Cancel;
        await raw.Connection.WriteFrameAsync(Http2FrameType.RstStream, 1, 0, cancel);
        await Task.Delay(500);

        releaseResponse.SetResult();
        await responseSent.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(500);

        foreach (var (method, url, version) in seen)
        {
            Assert.IsFalse(string.IsNullOrEmpty(method) || version.Major == 0 || url == "http://",
                $"BeforeResponse ran for an orphan session (method='{method}', url='{url}', version={version}).");
        }
    }
}
