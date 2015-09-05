using System;
using System.Text;
using System.IO;
using System.Net;
using Titanium.Web.Proxy.Helpers;
using System.Net.Sockets;
using Titanium.Web.Proxy.Exceptions;


namespace Titanium.Web.Proxy.Models
{
    public class SessionEventArgs : EventArgs, IDisposable
    {

        internal int BUFFER_SIZE;

        internal TcpClient client { get; set; }
        internal Stream clientStream { get; set; }
        internal CustomBinaryReader clientStreamReader { get; set; }
        internal StreamWriter clientStreamWriter { get; set; }

        internal string httpsHostName { get; set; }
        internal string httpsDecoratedHostName { get; set; }
        internal int requestContentLength { get; set; }
        internal Encoding requestEncoding { get; set; }
        internal Version requestHttpVersion { get; set; }
        internal bool requestIsAlive { get; set; }
        internal bool cancelRequest { get; set; }
        internal string requestBody { get; set; }
        internal bool requestBodyRead { get; set; }

        internal Encoding responseEncoding { get; set; }
        internal Stream responseStream { get; set; }
        internal string responseBody { get; set; }
        internal bool responseBodyRead { get; set; }

        internal HttpWebRequest proxyRequest { get; set; }
        internal HttpWebResponse serverResponse { get; set; }

        public int ClientPort { get; set; }
        public IPAddress ClientIpAddress { get; set; }

        public bool IsHttps { get; set; }
        public string RequestURL { get; set; }
        public string RequestHostname { get; set; }
        public string RequestMethod { get { return this.proxyRequest.Method; } }
        public int RequestContentLength { get { return requestContentLength; } }

        public HttpStatusCode ResponseStatusCode { get { return this.serverResponse.StatusCode; } }
        public string ResponseContentType { get { return this.serverResponse.ContentType; } }
        
       

        internal SessionEventArgs(int bufferSize)
        {
            BUFFER_SIZE = bufferSize;
        }

        public void Dispose()
        {
            if (this.proxyRequest != null)
                this.proxyRequest.Abort();

            if (this.responseStream != null)
                this.responseStream.Dispose();

            if (this.serverResponse != null)
                this.serverResponse.Close();
        }


        public string GetRequestBody()
        {
            if ((proxyRequest.Method.ToUpper() == "POST" || proxyRequest.Method.ToUpper() == "PUT") && requestContentLength > 0)
            {
                if (requestBody == null)
                {
                    var buffer = clientStreamReader.ReadBytes(requestContentLength);
                    requestBody = requestEncoding.GetString(buffer);
                }
                requestBodyRead = true;
                return requestBody;
            }
            else
                throw new BodyNotFoundException("Request don't have a body." +
            "Please verify that this request is a Http POST/PUT and request content length is greater than zero before accessing the body.");
      
        }
        public void SetRequestBody(string body)
        {
            this.requestBody = body;
            requestBodyRead = true;
        }
        public string GetResponseBody()
        {
            if (responseBody == null)
            {

                if (responseEncoding == null) responseEncoding = Encoding.GetEncoding(serverResponse.CharacterSet);
                if (responseEncoding == null) responseEncoding = Encoding.Default;


                switch (serverResponse.ContentEncoding)
                {
                    case "gzip":
                        responseBody = CompressionHelper.DecompressGzip(responseStream, responseEncoding);
                        break;
                    case "deflate":
                        responseBody = CompressionHelper.DecompressDeflate(responseStream, responseEncoding);
                        break;
                    case "zlib":
                        responseBody = CompressionHelper.DecompressZlib(responseStream, responseEncoding);
                        break;
                    default:
                        responseBody = DecodeData(responseStream, responseEncoding);
                        break;
                }

                responseBodyRead = true;

            }
            return responseBody;
        }

        public void SetResponseBody(string body)
        {
            if (responseEncoding == null) responseEncoding = Encoding.GetEncoding(serverResponse.CharacterSet);
            if (responseEncoding == null) responseEncoding = Encoding.Default;

            this.responseBody = body;
            responseBodyRead = true;
        }
        //stream reader not recomended for images
        private string DecodeData(Stream responseStream, Encoding e)
        {
            StreamReader reader = new StreamReader(responseStream, e);
            return reader.ReadToEnd();

        }

        public void Ok(string html)
        {

            if (html == null)
                html = string.Empty;

            var result = Encoding.Default.GetBytes(html);

            StreamWriter connectStreamWriter = new StreamWriter(clientStream);
            var s = String.Format("HTTP/{0}.{1} {2} {3}", requestHttpVersion.Major, requestHttpVersion.Minor, 200, "Ok");
            connectStreamWriter.WriteLine(s);
            connectStreamWriter.WriteLine(String.Format("Timestamp: {0}", DateTime.Now.ToString()));
            connectStreamWriter.WriteLine("content-length: " + result.Length);
            connectStreamWriter.WriteLine("Cache-Control: no-cache, no-store, must-revalidate");
            connectStreamWriter.WriteLine("Pragma: no-cache");
            connectStreamWriter.WriteLine("Expires: 0");

            if (requestIsAlive)
            {
                connectStreamWriter.WriteLine("Connection: Keep-Alive");
            }
            else
                connectStreamWriter.WriteLine("Connection: close");

            connectStreamWriter.WriteLine();
            connectStreamWriter.Flush();

            clientStream.Write(result, 0, result.Length);


            cancelRequest = true;

        }


    }

}