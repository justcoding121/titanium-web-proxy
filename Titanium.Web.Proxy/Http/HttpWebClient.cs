using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Shared;
using Titanium.Web.Proxy.Tcp;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// Used to communicate with the server over HTTP(S)
    /// </summary>
    public class HttpWebClient
    {
        private int processId;

        /// <summary>
        /// Connection to server
        /// </summary>
        internal TcpConnection ServerConnection { get; set; }


        public List<HttpHeader> ConnectHeaders { get; set; }
        public Request Request { get; set; }
        public Response Response { get; set; }

        /// <summary>
        /// PID of the process that is created the current session
        /// </summary>
        public int ProcessId
        {
            get
            {
                if (processId == 0)
                {
                    TcpRow tcpRow = TcpHelper.GetExtendedTcpTable().TcpRows.FirstOrDefault(row => row.LocalEndPoint.Port == ServerConnection.port);

                    processId = tcpRow?.ProcessId ?? -1;
                }

                return processId;
            }
        }

        /// <summary>
        /// Is Https?
        /// </summary>
        public bool IsHttps => this.Request.RequestUri.Scheme == Uri.UriSchemeHttps;

	    /// <summary>
        /// Set the tcp connection to server used by this webclient
        /// </summary>
        /// <param name="connection">Instance of <see cref="TcpConnection"/></param>
        internal void SetConnection(TcpConnection connection)
        {
            connection.LastAccess = DateTime.Now;
            ServerConnection = connection;
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
            if ((ServerConnection.UpStreamHttpProxy != null && ServerConnection.IsHttps == false) || (ServerConnection.UpStreamHttpsProxy != null && ServerConnection.IsHttps == true))
            {
                requestLines.AppendLine(string.Join(" ", this.Request.Method, this.Request.RequestUri.AbsoluteUri, $"HTTP/{this.Request.HttpVersion.Major}.{this.Request.HttpVersion.Minor}"));
            }
            else
            {
                requestLines.AppendLine(string.Join(" ", this.Request.Method, this.Request.RequestUri.PathAndQuery, $"HTTP/{this.Request.HttpVersion.Major}.{this.Request.HttpVersion.Minor}"));
            }

            //Send Authentication to Upstream proxy if needed
            if (ServerConnection.UpStreamHttpProxy != null && ServerConnection.IsHttps == false && !string.IsNullOrEmpty(ServerConnection.UpStreamHttpProxy.UserName) && ServerConnection.UpStreamHttpProxy.Password != null)
            {
                requestLines.AppendLine("Proxy-Connection: keep-alive");
                requestLines.AppendLine("Proxy-Authorization" + ": Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(ServerConnection.UpStreamHttpProxy.UserName + ":" + ServerConnection.UpStreamHttpProxy.Password)));
            }
            //write request headers
            foreach (var headerItem in this.Request.RequestHeaders)
            {
                var header = headerItem.Value;
                if (headerItem.Key != "Proxy-Authorization")
                {
                    requestLines.AppendLine(header.Name + ':' + header.Value);
                }
            }

            //write non unique request headers
            foreach (var headerItem in this.Request.NonUniqueRequestHeaders)
            {
                var headers = headerItem.Value;
                foreach (var header in headers)
                {
                    if (headerItem.Key != "Proxy-Authorization")
                    {
                        requestLines.AppendLine(header.Name + ':' + header.Value);
                    }
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

	                var nonUniqueHeaders = new List<HttpHeader> {existing, newHeader};

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