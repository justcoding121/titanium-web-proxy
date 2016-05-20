using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http
{
    public class HttpWebSession
    {
        internal TcpConnection ProxyClient { get; set; }

        public Request Request { get; set; }
        public Response Response { get; set; }

        public bool IsSecure
        {
            get
            {
                return this.Request.RequestUri.Scheme == Uri.UriSchemeHttps;
            }
        }

        internal void SetConnection(TcpConnection Connection)
        {
            Connection.LastAccess = DateTime.Now;
            ProxyClient = Connection;
        }

        internal HttpWebSession()
        {
            this.Request = new Request();
            this.Response = new Response();
        }

        internal async Task SendRequest()
        {
            Stream stream = ProxyClient.Stream;

            StringBuilder requestLines = new StringBuilder();

            requestLines.AppendLine(string.Join(" ", new string[3]
              {
                this.Request.Method,
                this.Request.RequestUri.PathAndQuery,
                this.Request.HttpVersion
              }));

            foreach (HttpHeader httpHeader in this.Request.RequestHeaders)
            {
                requestLines.AppendLine(httpHeader.Name + ':' + httpHeader.Value);
            }

            requestLines.AppendLine();

            string request = requestLines.ToString();
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
            stream.Flush();

            if (ProxyServer.Enable100ContinueBehaviour)
                if (this.Request.ExpectContinue)
                {
                    var httpResult = (await ProxyClient.ServerStreamReader.ReadLineAsync()).Split(Constants.SpaceSplit, 3);
                    var responseStatusCode = httpResult[1].Trim();
                    var responseStatusDescription = httpResult[2].Trim();

                    //find if server is willing for expect continue
                    if (responseStatusCode.Equals("100")
                    && responseStatusDescription.ToLower().Equals("continue"))
                    {
                        this.Request.Is100Continue = true;
                        await ProxyClient.ServerStreamReader.ReadLineAsync();
                    }
                    else if (responseStatusCode.Equals("417")
                         && responseStatusDescription.ToLower().Equals("expectation failed"))
                    {
                        this.Request.ExpectationFailed = true;
                        await ProxyClient.ServerStreamReader.ReadLineAsync();
                    }
                }
        }

        internal async Task ReceiveResponse()
        {
            //return if this is already read
            if (this.Response.ResponseStatusCode != null) return;

            var httpResult = (await ProxyClient.ServerStreamReader.ReadLineAsync()).Split(Constants.SpaceSplit, 3);

            if (string.IsNullOrEmpty(httpResult[0]))
            {
                await ProxyClient.ServerStreamReader.ReadLineAsync();
            }

            this.Response.HttpVersion = httpResult[0].Trim();
            this.Response.ResponseStatusCode = httpResult[1].Trim();
            this.Response.ResponseStatusDescription = httpResult[2].Trim();

            //For HTTP 1.1 comptibility server may send expect-continue even if not asked for it in request
            if (this.Response.ResponseStatusCode.Equals("100")
                && this.Response.ResponseStatusDescription.ToLower().Equals("continue"))
            {
                this.Response.Is100Continue = true;
                this.Response.ResponseStatusCode = null;
                await ProxyClient.ServerStreamReader.ReadLineAsync();
                await ReceiveResponse();
                return;
            }
            else if (this.Response.ResponseStatusCode.Equals("417")
                 && this.Response.ResponseStatusDescription.ToLower().Equals("expectation failed"))
            {
                this.Response.ExpectationFailed = true;
                this.Response.ResponseStatusCode = null;
                await ProxyClient.ServerStreamReader.ReadLineAsync();
                await ReceiveResponse();
                return;
            }

            List<string> responseLines = await ProxyClient.ServerStreamReader.ReadAllLinesAsync();

            for (int index = 0; index < responseLines.Count; ++index)
            {
                string[] strArray = responseLines[index].Split(Constants.ColonSplit, 2);
                this.Response.ResponseHeaders.Add(new HttpHeader(strArray[0], strArray[1]));
            }
        }
    }

}
