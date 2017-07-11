using StreamExtended;
using System;
using System.Net;

namespace Titanium.Web.Proxy.Http
{
    public class ConnectResponse : Response
    {
        public ServerHelloInfo ServerHelloInfo { get; set; }

        /// <summary>
        /// Creates a successfull CONNECT response
        /// </summary>
        /// <param name="httpVersion"></param>
        /// <returns></returns>
        internal static ConnectResponse CreateSuccessfullConnectResponse(Version httpVersion)
        {
            var response = new ConnectResponse
            {
                HttpVersion = httpVersion,
                ResponseStatusCode = (int)HttpStatusCode.OK,
                ResponseStatusDescription = "Connection Established"
            };

            response.ResponseHeaders.AddHeader("Timestamp", DateTime.Now.ToString());
            return response;
        }
    }
}
