using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {

        /// <summary>
        ///     Handle upgrade to websocket
        /// </summary>
        private async Task handleWebSocketUpgrade(string requestHttpMethod, string requestHttpUrl, Version requestVersion,
            SessionEventArgs args, Request request, Response response,
            HttpClientStream clientStream, TcpServerConnection serverConnection,
            CancellationTokenSource cancellationTokenSource, CancellationToken cancellationToken)
        {
            // prepare the prefix content
            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteRequestLine(requestHttpMethod, requestHttpUrl, requestVersion);
            headerBuilder.WriteHeaders(request.Headers);
            await serverConnection.Stream.WriteHeadersAsync(headerBuilder, cancellationToken);

            string httpStatus;
            try
            {
                httpStatus = await serverConnection.Stream.ReadLineAsync(cancellationToken)
                             ?? throw new ServerConnectionException("Server connection was closed.");
            }
            catch (Exception e) when (!(e is ServerConnectionException))
            {
                throw new ServerConnectionException("Server connection was closed.", e);
            }

            Response.ParseResponseLine(httpStatus, out var responseVersion,
                out int responseStatusCode,
                out string responseStatusDescription);
            response.HttpVersion = responseVersion;
            response.StatusCode = responseStatusCode;
            response.StatusDescription = responseStatusDescription;

            await HeaderParser.ReadHeaders(serverConnection.Stream, response.Headers,
                cancellationToken);

            if (!args.IsTransparent)
            {
                await clientStream.WriteResponseAsync(response,
                    cancellationToken: cancellationToken);
            }

            // If user requested call back then do it
            if (!args.HttpClient.Response.Locked)
            {
                await onBeforeResponse(args);
            }

            await TcpHelper.SendRaw(clientStream, serverConnection.Stream, BufferPool,
                args.OnDataSent, args.OnDataReceived, cancellationTokenSource, ExceptionFunc);
        }
    }
}
