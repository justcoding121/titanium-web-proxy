using System;
using System.Net;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     Raised when optional pre-200 upstream connectivity verification fails for a CONNECT
///     tunnel (<see cref="TunnelConnectSessionEventArgs.EstablishServerConnectionBeforeResponse" />).
///     Handlers may replace <see cref="Response" /> with a custom HTTP error before it is written
///     to the client (no TLS has been started yet).
/// </summary>
public class TunnelConnectFailureEventArgs : ProxyEventArgsBase
{
    internal TunnelConnectFailureEventArgs(ProxyServer server, TcpClientConnection clientConnection,
        TunnelConnectSessionEventArgs session, Exception exception)
        : base(server, clientConnection)
    {
        Session = session;
        Exception = exception;
        Response = CreateDefaultResponse(session, exception);
    }

    /// <summary>
    ///     The CONNECT session that failed upstream connectivity verification.
    /// </summary>
    public TunnelConnectSessionEventArgs Session { get; }

    /// <summary>
    ///     The exception from DNS/TCP/upstream-proxy CONNECT.
    ///     May be <see cref="UpstreamProxyConnectException" /> when an HTTP upstream rejected CONNECT.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    ///     HTTP response that will be sent to the client instead of 200 Connection Established.
    ///     Replace to customize status/body. Defaults to 502, or the upstream status when
    ///     <see cref="Exception" /> is <see cref="UpstreamProxyConnectException" />.
    /// </summary>
    public Response Response { get; set; }

    private static GenericResponse CreateDefaultResponse(TunnelConnectSessionEventArgs session, Exception exception)
    {
        if (exception is UpstreamProxyConnectException upstream)
        {
            var response = new GenericResponse(upstream.StatusCode,
                string.IsNullOrEmpty(upstream.StatusDescription) ? "Bad Gateway" : upstream.StatusDescription)
            {
                HttpVersion = session.HttpClient.Request.HttpVersion
            };
            if (!string.IsNullOrEmpty(upstream.BodyPreview))
                response.Body = System.Text.Encoding.UTF8.GetBytes(upstream.BodyPreview);
            return response;
        }

        var message = exception.GetBaseException().Message;
        var generic = new GenericResponse(HttpStatusCode.BadGateway)
        {
            HttpVersion = session.HttpClient.Request.HttpVersion,
            Body = System.Text.Encoding.UTF8.GetBytes(message)
        };
        return generic;
    }
}
