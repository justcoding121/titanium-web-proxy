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

        internal TcpClient Client { get; set; }
        internal Stream ClientStream { get; set; }
        internal CustomBinaryReader ClientStreamReader { get; set; }
        internal StreamWriter ClientStreamWriter { get; set; }

        internal string HttpsHostName { get; set; }
        internal string HttpsDecoratedHostName { get; set; }
        internal int RequestContentLength { get; set; }
        internal Encoding RequestEncoding { get; set; }
        internal Version RequestHttpVersion { get; set; }
        internal bool RequestIsAlive { get; set; }
        internal bool CancelRequest { get; set; }
        internal string RequestBody { get; set; }
        internal bool RequestBodyRead { get; set; }

        internal Encoding ResponseEncoding { get; set; }
        internal Stream ResponseStream { get; set; }
        internal string ResponseBody { get; set; }
        internal bool ResponseBodyRead { get; set; }

        public int ClientPort { get; set; }
        public IPAddress ClientIpAddress { get; set; }

        public bool IsHttps { get; set; }
        public string RequestURL { get; set; }
        public string RequestHostname { get; set; }

        public HttpWebRequest ProxyRequest { get; set; }
        public HttpWebResponse ServerResponse { get; set; }

        public void Dispose()
        {
            if (this.ProxyRequest != null)
                this.ProxyRequest.Abort();

            if (this.ResponseStream != null)
                this.ResponseStream.Dispose();

            if (this.ServerResponse != null)
                this.ServerResponse.Close();

        }

        public SessionEventArgs(int bufferSize)
        {
            BUFFER_SIZE = bufferSize;
        }
        public string GetRequestBody()
        {
            if ((ProxyRequest.Method.ToUpper() == "POST" || ProxyRequest.Method.ToUpper() == "PUT") && RequestContentLength > 0)
            {
                if (RequestBody == null)
                {
                    var buffer = ClientStreamReader.ReadBytes(RequestContentLength);
                    RequestBody = RequestEncoding.GetString(buffer);
                }
                RequestBodyRead = true;
                return RequestBody;
            }
            else
                throw new BodyNotFoundException("Request don't have a body." +
            "Please verify that this request is a Http POST/PUT and request content length is greater than zero before accessing the body.");
      
        }
        public void SetRequestBody(string body)
        {
            this.RequestBody = body;
            RequestBodyRead = true;
        }
        public string GetResponseBody()
        {
            if (ResponseBody == null)
            {

                if (ResponseEncoding == null) ResponseEncoding = Encoding.GetEncoding(ServerResponse.CharacterSet);
                if (ResponseEncoding == null) ResponseEncoding = Encoding.Default;


                switch (ServerResponse.ContentEncoding)
                {
                    case "gzip":
                        ResponseBody = CompressionHelper.DecompressGzip(ResponseStream, ResponseEncoding);
                        break;
                    case "deflate":
                        ResponseBody = CompressionHelper.DecompressDeflate(ResponseStream, ResponseEncoding);
                        break;
                    case "zlib":
                        ResponseBody = CompressionHelper.DecompressZlib(ResponseStream, ResponseEncoding);
                        break;
                    default:
                        ResponseBody = DecodeData(ResponseStream, ResponseEncoding);
                        break;
                }

                ResponseBodyRead = true;

            }
            return ResponseBody;
        }

        public void SetResponseBody(string body)
        {
            if (ResponseEncoding == null) ResponseEncoding = Encoding.GetEncoding(ServerResponse.CharacterSet);
            if (ResponseEncoding == null) ResponseEncoding = Encoding.Default;

            this.ResponseBody = body;
            ResponseBodyRead = true;
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

            StreamWriter connectStreamWriter = new StreamWriter(ClientStream);
            var s = String.Format("HTTP/{0}.{1} {2} {3}", RequestHttpVersion.Major, RequestHttpVersion.Minor, 200, "Ok");
            connectStreamWriter.WriteLine(s);
            connectStreamWriter.WriteLine(String.Format("Timestamp: {0}", DateTime.Now.ToString()));
            connectStreamWriter.WriteLine("content-length: " + result.Length);
            connectStreamWriter.WriteLine("Cache-Control: no-cache, no-store, must-revalidate");
            connectStreamWriter.WriteLine("Pragma: no-cache");
            connectStreamWriter.WriteLine("Expires: 0");

            if (RequestIsAlive)
            {
                connectStreamWriter.WriteLine("Connection: Keep-Alive");
            }
            else
                connectStreamWriter.WriteLine("Connection: close");

            connectStreamWriter.WriteLine();
            connectStreamWriter.Flush();

            ClientStream.Write(result, 0, result.Length);


            CancelRequest = true;

        }


    }

}