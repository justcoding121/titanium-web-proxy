using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Covers the HTTP/1.0 downgrade rule: HTTP/1.0 has no <c>chunked</c> transfer-coding at all, so
///     whenever <see cref="HttpWebClient.ResolveOriginHttpVersion" /> says the origin will be declared
///     "HTTP/1.0" on the wire (the default <see cref="OriginHttpVersionPolicy.PreserveClientVersion" />
///     mirrors an HTTP/1.0 client's own declared version), a request that is still
///     <see cref="Request.IsChunked" /> must be buffered and switched to <c>Content-Length</c> framing
///     before being forwarded - never relayed as <c>Transfer-Encoding: chunked</c> to a peer that cannot
///     parse it.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http10ChunkedDowngradeTests
{
    private const int SendReceiveTimeoutMs = 3000;

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task ChunkedHttp10Request_IsDowngradedToContentLength_BeforeReachingOrigin()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        var origin = new RawRequestCapturingOrigin();
        server.HandleTcpRequest(origin.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        // Default OriginHttpVersionPolicy (PreserveClientVersion): the origin-facing version mirrors the
        // client's own HTTP/1.0 declaration verbatim - exactly the case the downgrade rule must cover.
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        const string chunkPayload = "hello-from-a-chunked-http-1.0-request";

        using var client = new TcpClient("localhost", proxy.ProxyEndPoints[0].Port)
        {
            SendTimeout = SendReceiveTimeoutMs, ReceiveTimeout = SendReceiveTimeoutMs
        };
        var stream = client.GetStream();

        var requestHeaderText =
            "POST / HTTP/1.0\r\n" +
            "Host: localhost\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n";
        var chunkedBody =
            $"{chunkPayload.Length:x}\r\n{chunkPayload}\r\n0\r\n\r\n";

        var requestBytes = Encoding.ASCII.GetBytes(requestHeaderText + chunkedBody);
        await stream.WriteAsync(requestBytes);

        var buffer = new byte[4096];
        var received = new MemoryStream();
        var deadline = DateTime.UtcNow.AddMilliseconds(SendReceiveTimeoutMs * 5);
        while (received.Length == 0 || !Encoding.ASCII.GetString(received.ToArray()).Contains("\r\n\r\n"))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for a response from the proxy.");

            int read;
            try
            {
                read = await stream.ReadAsync(buffer);
            }
            catch (IOException)
            {
                break;
            }

            if (read == 0) break;
            received.Write(buffer, 0, read);
        }

        var rawResponse = Encoding.ASCII.GetString(received.ToArray());
        var statusLine = rawResponse.Length > 0
            ? rawResponse.Substring(0, rawResponse.IndexOf("\r\n", StringComparison.Ordinal))
            : string.Empty;
        Assert.IsTrue(statusLine.Contains("200"), $"Expected a 200 status line, got: '{statusLine}'.");

        Assert.IsTrue(origin.RequestReceived, "The origin double never observed a completed request.");
        Assert.IsFalse(origin.SawTransferEncodingHeader,
            "A 'Transfer-Encoding' header must never be forwarded to an origin declared as HTTP/1.0 - " +
            "the origin double observed one on the wire, meaning the downgrade did not happen.");
        Assert.IsTrue(origin.SawContentLengthHeader,
            "The downgraded request must carry a 'Content-Length' header instead of chunked framing.");
        Assert.AreEqual(chunkPayload.Length, origin.ObservedContentLength,
            "The declared Content-Length must match the fully-decoded (de-chunked) body length.");
        Assert.AreEqual(chunkPayload, origin.ObservedBody,
            "The de-chunked body bytes must reach the origin unchanged.");
    }

    /// <summary>
    ///     A minimal raw plain-HTTP origin double that captures whether the request it received carried a
    ///     'Transfer-Encoding' header, a 'Content-Length' header (and its declared value), and the body
    ///     bytes read according to that Content-Length - everything the downgrade-rule test needs to
    ///     assert on, without pulling in a full HTTP/1.1 request parser that itself understands chunked
    ///     request bodies.
    /// </summary>
    private sealed class RawRequestCapturingOrigin
    {
        public bool RequestReceived { get; private set; }
        public bool SawTransferEncodingHeader { get; private set; }
        public bool SawContentLengthHeader { get; private set; }
        public int ObservedContentLength { get; private set; }
        public string ObservedBody { get; private set; } = string.Empty;

        public async Task HandleRequest(ConnectionContext context)
        {
            try
            {
                var raw = new StringBuilder();
                var buffer = new byte[4096];

                int headerEnd;
                while ((headerEnd = IndexOfHeaderTerminator(raw)) < 0)
                {
                    var result = await context.Transport.Input.ReadAsync();
                    foreach (var seg in result.Buffer) raw.Append(Encoding.ASCII.GetString(seg.Span));
                    context.Transport.Input.AdvanceTo(result.Buffer.End);

                    if (result.IsCompleted && headerEnd < 0) return;
                }

                var headerText = raw.ToString(0, headerEnd);
                SawTransferEncodingHeader = ContainsHeader(headerText, "Transfer-Encoding");
                SawContentLengthHeader = ContainsHeader(headerText, "Content-Length");
                ObservedContentLength = ExtractContentLength(headerText);

                // Bytes already buffered past the header terminator (into raw) plus anything still to
                // arrive, up to the declared Content-Length.
                var bodySoFar = raw.ToString(headerEnd + 4, raw.Length - (headerEnd + 4));
                while (Encoding.ASCII.GetByteCount(bodySoFar) < ObservedContentLength)
                {
                    var result = await context.Transport.Input.ReadAsync();
                    foreach (var seg in result.Buffer) bodySoFar += Encoding.ASCII.GetString(seg.Span);
                    context.Transport.Input.AdvanceTo(result.Buffer.End);

                    if (result.IsCompleted) break;
                }

                ObservedBody = ObservedContentLength >= 0 && ObservedContentLength <= bodySoFar.Length
                    ? bodySoFar.Substring(0, ObservedContentLength)
                    : bodySoFar;
                RequestReceived = true;

                var responseText = "HTTP/1.0 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await context.Transport.Output.WriteAsync(Encoding.ASCII.GetBytes(responseText));
                context.Transport.Output.Complete();
            }
            catch
            {
                // best-effort test double; failures surface via the test's own assertions.
            }
        }

        private static int IndexOfHeaderTerminator(StringBuilder raw)
        {
            // StringBuilder has no IndexOf; the header block here is always tiny, so a materialized
            // scan is fine and keeps this double simple.
            var text = raw.ToString();
            return text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        }

        private static bool ContainsHeader(string headerText, string name)
        {
            foreach (var line in headerText.Split("\r\n"))
                if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        private static int ExtractContentLength(string headerText)
        {
            foreach (var line in headerText.Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Substring("Content-Length:".Length).Trim(), out var value))
                    return value;

            return -1;
        }
    }
}
