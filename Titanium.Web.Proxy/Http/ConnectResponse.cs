using System;
using System.Net;
using StreamExtended;

namespace Titanium.Web.Proxy.Http
{
    public class ConnectResponse : Response
    {
        public ServerHelloInfo ServerHelloInfo { get; set; }

        /// <summary>
        ///     Creates a successfull CONNECT response
        /// </summary>
        /// <param name="httpVersion"></param>
        /// <returns></returns>
        internal static ConnectResponse CreateSuccessfullConnectResponse(Version httpVersion)
        {
            var response = new ConnectResponse
            {
                HttpVersion = httpVersion,
                StatusCode = (int)HttpStatusCode.OK,
                StatusDescription = "Connection Established"
            };

            return response;
        }
    }
}
