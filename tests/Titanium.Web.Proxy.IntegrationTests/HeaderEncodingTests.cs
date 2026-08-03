using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Characterization for issue #855: non-ASCII header octets (e.g. Shift-JIS Content-Disposition)
///     must pass through the proxy unchanged on the wire.
/// </summary>
[TestClass]
public class HeaderEncodingTests
{
    // Shift-JIS bytes for エンコーディング.docx inside a Content-Disposition filename="..."
    // (the exact octets a Japanese origin would send before any Unicode interpretation).
    private static readonly byte[] ShiftJisFileName =
    {
        0x83, 0x47, 0x83, 0x93, 0x83, 0x52, 0x81, 0x5B, 0x83, 0x66, 0x83, 0x42, 0x83, 0x93, 0x83, 0x4F
    };

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task ContentDisposition_ShiftJisOctets_PassThroughUnchanged()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        var latin1 = HttpHeader.Encoding;
        var dispositionPrefix = latin1.GetBytes("attachment;filename=\"");
        var dispositionSuffix = latin1.GetBytes(".docx\"");
        var dispositionValue = new byte[dispositionPrefix.Length + ShiftJisFileName.Length + dispositionSuffix.Length];
        Buffer.BlockCopy(dispositionPrefix, 0, dispositionValue, 0, dispositionPrefix.Length);
        Buffer.BlockCopy(ShiftJisFileName, 0, dispositionValue, dispositionPrefix.Length, ShiftJisFileName.Length);
        Buffer.BlockCopy(dispositionSuffix, 0, dispositionValue,
            dispositionPrefix.Length + ShiftJisFileName.Length, dispositionSuffix.Length);

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            // Build a raw response with exact Latin-1/Shift-JIS octets in Content-Disposition.
            var headerName = latin1.GetBytes("Content-Disposition: ");
            var preamble = latin1.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n");
            var trailer = latin1.GetBytes("\r\n\r\nOK");

            var response = new byte[preamble.Length + headerName.Length + dispositionValue.Length + trailer.Length];
            var offset = 0;
            Buffer.BlockCopy(preamble, 0, response, offset, preamble.Length);
            offset += preamble.Length;
            Buffer.BlockCopy(headerName, 0, response, offset, headerName.Length);
            offset += headerName.Length;
            Buffer.BlockCopy(dispositionValue, 0, response, offset, dispositionValue.Length);
            offset += dispositionValue.Length;
            Buffer.BlockCopy(trailer, 0, response, offset, trailer.Length);

            await context.Transport.Output.WriteAsync(response);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();

        var request = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        var responseBytes = await ReadAllAsync(stream, TimeSpan.FromSeconds(10));
        var responseText = latin1.GetString(responseBytes);

        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 200", StringComparison.Ordinal),
            $"Expected 200 response. Got:\n{responseText}");

        // Locate Content-Disposition value octets in the raw response and compare exactly.
        var needle = latin1.GetBytes("Content-Disposition: ");
        var idx = IndexOf(responseBytes, needle);
        Assert.IsTrue(idx >= 0, "Content-Disposition header missing from proxied response.");

        var valueStart = idx + needle.Length;
        var valueEnd = valueStart;
        while (valueEnd + 1 < responseBytes.Length &&
               !(responseBytes[valueEnd] == (byte)'\r' && responseBytes[valueEnd + 1] == (byte)'\n'))
            valueEnd++;

        var actualValue = new byte[valueEnd - valueStart];
        Buffer.BlockCopy(responseBytes, valueStart, actualValue, 0, actualValue.Length);
        CollectionAssert.AreEqual(dispositionValue, actualValue,
            "Shift-JIS filename octets in Content-Disposition must pass through unchanged.");
    }

    private static async Task DrainRequestHeaders(ConnectionContext context)
    {
        var encoding = HttpHelper.GetEncodingFromContentType(null);
        var requestText = string.Empty;
        while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var result = await context.Transport.Input.ReadAsync();
            foreach (var seg in result.Buffer) requestText += encoding.GetString(seg.Span);
            context.Transport.Input.AdvanceTo(result.Buffer.End);
        }
    }

    private static async Task<byte[]> ReadAllAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new System.Threading.CancellationTokenSource(timeout);
        using var ms = new System.IO.MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
                ms.Write(buffer, 0, read);
        }
        catch (OperationCanceledException)
        {
            // timeout with partial data is fine for assertion
        }

        return ms.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match) return i;
        }

        return -1;
    }
}