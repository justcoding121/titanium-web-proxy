#pragma warning disable TWP001
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2HelperSendFramesWave42FatGapTests
{


    [TestMethod]
    public async Task SendHeaderTrailerBodyAndData_WriteExpectedFrames()
    {
        var settings = new Http2Settings();
        var (header, buf) = Wave42FatGapTestSupport.FrameScratch(3);
        var flow = new Http2FlowController();

        using var headers = new MemoryStream();
        var request = new Request
        {
            Method = "POST",
            HttpVersion = HttpHeader.Version20,
            IsHttps = true,
            Authority = "api.example".GetByteString(),
            RequestUriString8 = "/upload".GetByteString()
        };
        request.Headers.AddHeader("content-type", "application/octet-stream");
        await Http2Helper.SendHeader(settings, header, buf, request, endStream: false, headers, pushPromise: false);
        Assert.AreEqual((byte)Http2FrameType.Headers, headers.ToArray()[3]);

        using var push = new MemoryStream();
        await Http2Helper.SendHeader(settings, header, buf, request, endStream: true, push, pushPromise: true);
        Assert.AreEqual((byte)Http2FrameType.PushPromise, push.ToArray()[3]);

        using var trailers = new MemoryStream();
        var trailing = new HeaderCollection();
        trailing.AddHeader("ETag", "abc");
        await Http2QueuedSend.SendTrailer(settings, header, buf, 3, trailing, endStream: true, trailers);
        Assert.IsTrue(trailers.Length > 9);

        var response = new Response
        {
            HttpVersion = HttpHeader.Version20,
            StatusCode = 200
        };
        response.Body = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        response.IsBodyRead = true;
        using var body = new MemoryStream();
        await Http2QueuedSend.SendBody(settings, response, header, buf, 4, flow, body);
        Assert.IsTrue(body.Length > 9 + 8);

        using var data = new MemoryStream();
        using var gate = new SemaphoreSlim(1, 1);
        await Http2Helper.SendData(header, buf, 5, ReadOnlyMemory<byte>.Empty, endStream: true, maxFrameSize: 0,
            flow, data, CancellationToken.None, gate);
        await Http2Helper.SendData(header, buf, 5, new byte[] { 9, 8, 7 }, endStream: true, maxFrameSize: 2,
            flow, data, CancellationToken.None, gate);
        Assert.IsTrue(data.Length > 9);
    }
}
#pragma warning restore TWP001
