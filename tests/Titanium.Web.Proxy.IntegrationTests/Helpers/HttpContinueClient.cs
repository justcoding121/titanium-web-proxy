using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

internal class HttpContinueClient
{
    /// <summary>
    ///     Default wait for a 100-continue / final response. Kept generous for shared CI runners
    ///     under socket/TLS load; the deadlock baseline test passes a short timeout explicitly.
    /// </summary>
    private const int DefaultWaitTimeoutMs = 5_000;

    private static readonly Encoding _msgEncoding = HttpHelper.GetEncodingFromContentType(null);

    public static Task<Response?> Post(string server, int port, string content) =>
        Post(server, port, content, DefaultWaitTimeoutMs);

    public static async Task<Response?> Post(string server, int port, string content, int waitTimeoutMs)
    {
        var message = _msgEncoding.GetBytes(content);
        var client = new TcpClient(server, port);
        client.SendTimeout = client.ReceiveTimeout = waitTimeoutMs;

        var request = new Request { Method = "POST", RequestUriString = "/", HttpVersion = new Version(1, 1) };
        request.Headers.AddHeader(KnownHeaders.Host, server);
        request.Headers.AddHeader(KnownHeaders.ContentLength, message.Length.ToString());
        request.Headers.AddHeader(KnownHeaders.Expect, KnownHeaders.Expect100Continue);

        var header = _msgEncoding.GetBytes(request.HeaderText);
        await client.GetStream().WriteAsync(header);

        var buffer = new byte[1024];
        var responseMsg = string.Empty;
        Response? response;

        while ((response = HttpMessageParsing.ParseResponse(responseMsg)) == null)
        {
            var readTask = client.GetStream().ReadAsync(buffer.AsMemory(0, 1024)).AsTask();
            if (!readTask.Wait(waitTimeoutMs))
            {
                return null;
            }

            responseMsg += _msgEncoding.GetString(buffer, 0, readTask.Result);
        }

        if (response.StatusCode == 100)
        {
            await client.GetStream().WriteAsync(message);

            responseMsg = string.Empty;

            while ((response = HttpMessageParsing.ParseResponse(responseMsg)) == null)
            {
                var readTask = client.GetStream().ReadAsync(buffer.AsMemory(0, 1024)).AsTask();
                if (!readTask.Wait(waitTimeoutMs))
                {
                    return null;
                }

                responseMsg += _msgEncoding.GetString(buffer, 0, readTask.Result);
            }

            return response;
        }

        return response;
    }
}
