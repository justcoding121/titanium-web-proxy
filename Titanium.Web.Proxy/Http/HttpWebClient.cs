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
    /// <summary>
    /// Used to communicate with the server over HTTP(S)
    /// </summary>
    public class HttpWebClient
    {
        /// <summary>
        /// Connection to server
        /// </summary>
        internal TcpConnection ServerConnection { get; set; }

        public Request Request { get; set; }
        public Response Response { get; set; }

        /// <summary>
        /// Is Https?
        /// </summary>
        public bool IsHttps
        {
            get
            {
                return this.Request.RequestUri.Scheme == Uri.UriSchemeHttps;
            }
        }

        /// <summary>
        /// Set the tcp connection to server used by this webclient
        /// </summary>
        /// <param name="Connection"></param>
        internal void SetConnection(TcpConnection Connection)
        {
            ServerConnection = Connection;
        }

        internal HttpWebClient()
        {
            this.Request = new Request();
            this.Response = new Response();
        }

        /// <summary>
        /// Prepare & send the http(s) request
        /// </summary>
        /// <returns></returns>
        internal async Task SendRequest(bool enable100ContinueBehaviour)
        {
            Stream stream = ServerConnection.Stream;

            StringBuilder requestLines = new StringBuilder();

            //prepare the request & headers
            requestLines.AppendLine(string.Join(" ", new string[3]
              {
                this.Request.Method,
                this.Request.RequestUri.PathAndQuery,
                string.Format("HTTP/{0}.{1}",this.Request.HttpVersion.Major, this.Request.HttpVersion.Minor)
              }));

            //write request headers
            foreach (var headerItem in this.Request.RequestHeaders)
            {
                var header = headerItem.Value;
                requestLines.AppendLine(header.Name + ':' + header.Value);
            }

            //write non unique request headers
            foreach (var headerItem in this.Request.NonUniqueRequestHeaders)
            {
                var headers = headerItem.Value;
                foreach (var header in headers)
                {
                    requestLines.AppendLine(header.Name + ':' + header.Value);
                }
            }

            requestLines.AppendLine();

            string request = requestLines.ToString();
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
            await stream.FlushAsync();

            if (enable100ContinueBehaviour)
            {
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
                        await ServerConnection.StreamReader.ReadLineAsync();
                    }
                    else if (responseStatusCode.Equals("417")
                         && responseStatusDescription.ToLower().Equals("expectation failed"))
                    {
                        this.Request.ExpectationFailed = true;
                        await ServerConnection.StreamReader.ReadLineAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Receive & parse the http response from server
        /// </summary>
        /// <returns></returns>
        internal async Task ReceiveResponse()
        {
            //return if this is already read
            if (this.Response.ResponseStatusCode != null) return;

            var httpResult = (await ServerConnection.StreamReader.ReadLineAsync()).Split(ProxyConstants.SpaceSplit, 3);

            if (string.IsNullOrEmpty(httpResult[0]))
            {
                //Empty content in first-line, try again
                httpResult = (await ServerConnection.StreamReader.ReadLineAsync()).Split(ProxyConstants.SpaceSplit, 3);
            }

            var httpVersion = httpResult[0].Trim().ToLower();

            var version = new Version(1, 1);
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
                //Read the next line after 100-continue 
                this.Response.Is100Continue = true;
                this.Response.ResponseStatusCode = null;
                await ServerConnection.StreamReader.ReadLineAsync();
                //now receive response
                await ReceiveResponse();
                return;
            }
            else if (this.Response.ResponseStatusCode.Equals("417")
                 && this.Response.ResponseStatusDescription.ToLower().Equals("expectation failed"))
            {
                //read next line after expectation failed response
                this.Response.ExpectationFailed = true;
                this.Response.ResponseStatusCode = null;
                await ServerConnection.StreamReader.ReadLineAsync();
                //now receive response 
                await ReceiveResponse();
                return;
            }

            //Read the Response headers
            //Read the response headers in to unique and non-unique header collections
            string tmpLine;
            while (!string.IsNullOrEmpty(tmpLine = await ServerConnection.StreamReader.ReadLineAsync()))
            {
                var header = tmpLine.Split(ProxyConstants.ColonSplit, 2);

                var newHeader = new HttpHeader(header[0], header[1]);

                //if header exist in non-unique header collection add it there
                if (Response.NonUniqueResponseHeaders.ContainsKey(newHeader.Name))
                {
                    Response.NonUniqueResponseHeaders[newHeader.Name].Add(newHeader);
                }
                //if header is alread in unique header collection then move both to non-unique collection
                else if (Response.ResponseHeaders.ContainsKey(newHeader.Name))
                {
                    var existing = Response.ResponseHeaders[newHeader.Name];

                    var nonUniqueHeaders = new List<HttpHeader>();

                    nonUniqueHeaders.Add(existing);
                    nonUniqueHeaders.Add(newHeader);

                    Response.NonUniqueResponseHeaders.Add(newHeader.Name, nonUniqueHeaders);
                    Response.ResponseHeaders.Remove(newHeader.Name);
                }
                //add to unique header collection
                else
                {
                    Response.ResponseHeaders.Add(newHeader.Name, newHeader);
                }
            }
        }
    }

}
