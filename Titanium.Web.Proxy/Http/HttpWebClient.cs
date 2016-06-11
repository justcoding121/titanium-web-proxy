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
    public class HttpWebClient
    {
        internal TcpConnection ServerConnection { get; set; }

        public Request Request { get; set; }
        public Response Response { get; set; }

        public bool IsHttps
        {
            get
            {
                return this.Request.RequestUri.Scheme == Uri.UriSchemeHttps;
            }
        }

        internal void SetConnection(TcpConnection Connection)
        {
            Connection.LastAccess = DateTime.Now;
            ServerConnection = Connection;
        }

        internal HttpWebClient()
        {
            this.Request = new Request();
            this.Response = new Response();
        }

        internal async Task SendRequest()
        {
            Stream stream = ServerConnection.Stream;

            StringBuilder requestLines = new StringBuilder();

            requestLines.AppendLine(string.Join(" ", new string[3]
              {
                this.Request.Method,
                this.Request.RequestUri.PathAndQuery,
                string.Format("HTTP/{0}.{1}",this.Request.HttpVersion.Major, this.Request.HttpVersion.Minor)
              }));

            foreach (HttpHeader httpHeader in this.Request.RequestHeaders)
            {
                requestLines.AppendLine(httpHeader.Name + ':' + httpHeader.Value);
            }

            requestLines.AppendLine();

            string request = requestLines.ToString();
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
            await stream.FlushAsync();

            if (ProxyServer.Enable100ContinueBehaviour)
                if (this.Request.ExpectContinue)
                {
                    var httpResult = (await ServerConnection.StreamReader.ReadLineAsync()).Split(ProxyConstants.SpaceSplit, 3);
                    var responseStatusCode = httpResult[1].Trim();
                    var responseStatusDescription = httpResult[2].Trim();

                    //find if server is willing for expect continue
                    if (responseStatusCode.Equals("100")
                    && responseStatusDescription.ToLower().Equals("continue"))
                    {
                        this.Request.Is100Continue = true;
                        await ServerConnection.StreamReader.ReadLineAsync().ConfigureAwait(false);
                    }
                    else if (responseStatusCode.Equals("417")
                         && responseStatusDescription.ToLower().Equals("expectation failed"))
                    {
                        this.Request.ExpectationFailed = true;
                        await ServerConnection.StreamReader.ReadLineAsync().ConfigureAwait(false);
                    }
                }
        }

        internal async Task ReceiveResponse()
        {
            //return if this is already read
            if (this.Response.ResponseStatusCode != null) return;

            var httpResult = (await ServerConnection.StreamReader.ReadLineAsync()).Split(ProxyConstants.SpaceSplit, 3);

            if (string.IsNullOrEmpty(httpResult[0]))
            {
                await ServerConnection.StreamReader.ReadLineAsync().ConfigureAwait(false);
            }
            var httpVersion = httpResult[0].Trim().ToLower();

            var version = new Version(1,1);
            if (httpVersion == "http/1.0")
            {
                version = new Version(1, 0);
            }

            this.Response.HttpVersion = version;
            this.Response.ResponseStatusCode = httpResult[1].Trim();
            this.Response.ResponseStatusDescription = httpResult[2].Trim();

            //For HTTP 1.1 comptibility server may send expect-continue even if not asked for it in request
            if (this.Response.ResponseStatusCode.Equals("100")
                && this.Response.ResponseStatusDescription.ToLower().Equals("continue"))
            {
                this.Response.Is100Continue = true;
                this.Response.ResponseStatusCode = null;
                await ServerConnection.StreamReader.ReadLineAsync().ConfigureAwait(false);
                await ReceiveResponse();
                return;
            }
            else if (this.Response.ResponseStatusCode.Equals("417")
                 && this.Response.ResponseStatusDescription.ToLower().Equals("expectation failed"))
            {
                this.Response.ExpectationFailed = true;
                this.Response.ResponseStatusCode = null;
                await ServerConnection.StreamReader.ReadLineAsync().ConfigureAwait(false);
                await ReceiveResponse();
                return;
            }

            List<string> responseLines = await ServerConnection.StreamReader.ReadAllLinesAsync().ConfigureAwait(false);

            for (int index = 0; index < responseLines.Count; ++index)
            {
                string[] strArray = responseLines[index].Split(ProxyConstants.ColonSplit, 2);
                this.Response.ResponseHeaders.Add(new HttpHeader(strArray[0], strArray[1]));
            }
        }
    }

}
