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
public class Http2HelperEncodeHeaderBlockWave42FatGapTests
{


    [TestMethod]
    public async Task EncodeHeaderBlock_CoversRequestResponsePriorityViaAndTableSize()
    {
        var settings = new Http2Settings { MaxFrameSize = 32 };
        var (header, buf) = Wave42FatGapTestSupport.FrameScratch(1);

        using var actual = new MemoryStream();
        await using (var writer = new Http2FrameWriter(actual))
        {
            foreach (var method in new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "CONNECT", "PATCH" })
            {
                var request = new Request
                {
                    Method = method,
                    HttpVersion = HttpHeader.Version20,
                    IsHttps = method != "CONNECT",
                    RequestUriString8 = "/item".GetByteString(),
                    Authority = "origin.example:443".GetByteString(),
                    ExtendedConnectProtocol = method == "CONNECT" ? "websocket" : null,
                    Priority = method == "GET" ? 0x0102030405L : null
                };
                request.Headers.AddHeader("Content-Type", "text/plain");
                request.Headers.AddHeader("Via", "2.0 twp");
                Http2Helper.EnqueueHeader(settings, header, buf, request, endStream: true, writer);
            }

            settings.UpdateHeaderTableSize(0);
            settings.UpdateHeaderTableSize(4096);

            foreach (var status in new[] { 200, 204, 206, 301, 302, 304, 400, 404, 500, 502, 418 })
            {
                var response = new Response
                {
                    HttpVersion = HttpHeader.Version20,
                    StatusCode = status,
                    StatusDescription = "x"
                };
                response.Headers.AddHeader("X-Mixed-Case", "kept");
                Http2Helper.EnqueueHeader(settings, header, buf, response, endStream: true, writer);
            }

            var hostOnly = new Request
            {
                Method = "GET",
                HttpVersion = HttpHeader.Version20,
                IsHttps = false,
                Host = "fallback.example",
                RequestUriString8 = "/".GetByteString()
            };
            Http2Helper.EnqueueHeader(settings, header, buf, hostOnly, endStream: true, writer);

            var trailers = new HeaderCollection();
            trailers.AddHeader("X-Trailer", "yes");
            trailers.AddHeader("grpc-status", "0");
            Http2Helper.EnqueueTrailer(settings, header, buf, 1, trailers, endStream: true, writer);

            Http2Helper.EnqueueSettingsAck(writer);
            Http2Helper.EnqueuePingAck(writer, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Http2Helper.EnqueueDataFrames(writer, 1, ReadOnlyMemory<byte>.Empty, endStream: true, maxFrameSize: 0);
        }

        Assert.IsTrue(actual.Length > 200);
    }
}
#pragma warning restore TWP001
