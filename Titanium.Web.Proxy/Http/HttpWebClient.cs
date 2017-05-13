using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
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

        /// <summary>
        /// Request ID.
        /// </summary>
        public Guid RequestId { get; }
        /// <summary>
        /// Headers passed with Connect.
        /// </summary>
        public List<HttpHeader> ConnectHeaders { get; set; }
        /// <summary>
        /// Web Request.
        /// </summary>
        public Request Request { get; set; }
        /// <summary>
        /// Web Response.
        /// </summary>
        public Response Response { get; set; }

        /// <summary>
        /// PID of the process that is created the current session when client is running in this machine
        /// If client is remote then this will return 
        /// </summary>
        public Lazy<int> ProcessId { get; internal set; }

        /// <summary>
        /// Is Https?
        /// </summary>
        public bool IsHttps => Request.RequestUri.Scheme == Uri.UriSchemeHttps;


        internal HttpWebClient()
        {
            RequestId = Guid.NewGuid();
            Request = new Request();
            Response = new Response();
        }

        /// <summary>
        /// Set the tcp connection to server used by this webclient
        /// </summary>
        /// <param name="connection">Instance of <see cref="TcpConnection"/></param>
        internal void SetConnection(TcpConnection connection)
        {
            connection.LastAccess = DateTime.Now;
            ServerConnection = connection;
        }
  

        /// <summary>
        /// Prepare and send the http(s) request
        /// </summary>
        /// <returns></returns>
        internal async Task SendRequest(bool enable100ContinueBehaviour)
        {
            var stream = ServerConnection.Stream;

            var requestLines = new StringBuilder();

            //prepare the request & headers
            if ((ServerConnection.UpStreamHttpProxy != null && ServerConnection.IsHttps == false) || (ServerConnection.UpStreamHttpsProxy != null && ServerConnection.IsHttps))
            {
                requestLines.AppendLine($"{Request.Method} {Request.RequestUri.AbsoluteUri} HTTP/{Request.HttpVersion.Major}.{Request.HttpVersion.Minor}");
            }
            else
            {
                requestLines.AppendLine($"{Request.Method} {Request.RequestUri.PathAndQuery} HTTP/{Request.HttpVersion.Major}.{Request.HttpVersion.Minor}");
            }

            //Send Authentication to Upstream proxy if needed
            if (ServerConnection.UpStreamHttpProxy != null && ServerConnection.IsHttps == false && !string.IsNullOrEmpty(ServerConnection.UpStreamHttpProxy.UserName) && ServerConnection.UpStreamHttpProxy.Password != null)
            {
                requestLines.AppendLine("Proxy-Connection: keep-alive");
                requestLines.AppendLine("Proxy-Authorization" + ": Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                                            $"{ServerConnection.UpStreamHttpProxy.UserName}:{ServerConnection.UpStreamHttpProxy.Password}")));
            }
            //write request headers
            foreach (var headerItem in Request.RequestHeaders)
            {
                var header = headerItem.Value;
                if (headerItem.Key != "Proxy-Authorization")
                {
                    requestLines.AppendLine($"{header.Name}: {header.Value}");
                }
            }

            //write non unique request headers
            foreach (var headerItem in Request.NonUniqueRequestHeaders)
            {
                var headers = headerItem.Value;
                foreach (var header in headers)
                {
                    if (headerItem.Key != "Proxy-Authorization")
                    {
                        requestLines.AppendLine($"{header.Name}: {header.Value}");
                    }
                }
            }

            requestLines.AppendLine();

            var request = requestLines.ToString();
            var requestBytes = Encoding.ASCII.GetBytes(request);

            await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
            await stream.FlushAsync();

            if (enable100ContinueBehaviour)
            {
                if (Request.ExpectContinue)
                {
                    var httpResult = (await ServerConnection.StreamReader.ReadLineAsync()).Split(ProxyConstants.SpaceSplit, 3);
                    var responseStatusCode = httpResult[1].Trim();
                    var responseStatusDescription = httpResult[2].Trim();

                    //find if server is willing for expect continue
                    if (responseStatusCode.Equals("100")
                    && responseStatusDescription.Equals("continue", StringComparison.CurrentCultureIgnoreCase))
                    {
                        Request.Is100Continue = true;
                        await ServerConnection.StreamReader.ReadLineAsync();
                    }
                    else if (responseStatusCode.Equals("417")
                         && responseStatusDescription.Equals("expectation failed", StringComparison.CurrentCultureIgnoreCase))
                    {
                        Request.ExpectationFailed = true;
                        await ServerConnection.StreamReader.ReadLineAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Receive and parse the http response from server
        /// </summary>
        /// <returns></returns>
        internal async Task ReceiveResponse()
        {
            //return if this is already read
            if (Response.ResponseStatusCode != null) return;

            var httpResult = (await ServerConnection.StreamReader.ReadLineAsync()).Split(ProxyConstants.SpaceSplit, 3);

            if (string.IsNullOrEmpty(httpResult[0]))
            {
                //Empty content in first-line, try again
                httpResult = (await ServerConnection.StreamReader.ReadLineAsync()).Split(ProxyConstants.SpaceSplit, 3);
            }

            var httpVersion = httpResult[0].Trim().ToLower();

            var version = new Version(1, 1);
            if (0 == string.CompareOrdinal(httpVersion, "http/1.0"))
            {
                version = new Version(1, 0);
            }

            Response.HttpVersion = version;
            Response.ResponseStatusCode = httpResult[1].Trim();
            Response.ResponseStatusDescription = httpResult[2].Trim();

            //For HTTP 1.1 comptibility server may send expect-continue even if not asked for it in request
            if (Response.ResponseStatusCode.Equals("100")
                && Response.ResponseStatusDescription.Equals("continue", StringComparison.CurrentCultureIgnoreCase))
            {
                //Read the next line after 100-continue 
                Response.Is100Continue = true;
                Response.ResponseStatusCode = null;
                await ServerConnection.StreamReader.ReadLineAsync();
                //now receive response
                await ReceiveResponse();
                return;
            }
            else if (Response.ResponseStatusCode.Equals("417")
                 && Response.ResponseStatusDescription.Equals("expectation failed", StringComparison.CurrentCultureIgnoreCase))
            {
                //read next line after expectation failed response
                Response.ExpectationFailed = true;
                Response.ResponseStatusCode = null;
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