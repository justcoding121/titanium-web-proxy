using System;
using System.Text;
using System.IO;
using System.Net;
using Titanium.Web.Proxy.Helpers;
using System.Net.Sockets;


namespace Titanium.Web.Proxy.Models
{
    public class SessionEventArgs : EventArgs, IDisposable
    {

        internal int BUFFER_SIZE;

        internal TcpClient Client { get; set; }
        internal Stream ClientStream { get; set; }
        internal CustomBinaryReader ClientStreamReader { get; set; }
        internal StreamWriter ClientStreamWriter { get; set; }

        internal string UpgradeProtocol { get; set; }
        internal Encoding Encoding { get; set; }
        internal int RequestLength { get; set; }
        internal Version RequestHttpVersion { get; set; }
        internal bool RequestIsAlive { get; set; }
        internal bool CancelRequest { get; set; }
        internal string RequestHtmlBody { get; set; }
        internal bool RequestWasModified { get; set; }

        internal Stream ServerResponseStream { get; set; }
        internal string ResponseHtmlBody { get; set; }
        internal bool ResponseWasModified { get; set; }

        
        public int ClientPort { get; set; }
        public IPAddress ClientIpAddress { get; set; }
        public string tunnelHostName { get; set; }
        public string securehost { get; set; }
        public bool IsSSLRequest { get; set; }
        public string RequestURL { get; set; }
        public string RequestHostname { get; set; }

        public HttpWebRequest ProxyRequest { get; set; }
        public HttpWebResponse ServerResponse { get; set; }

        public void Dispose()
        {
            if (this.ProxyRequest != null)
                this.ProxyRequest.Abort();

            if (this.ServerResponseStream != null)
                this.ServerResponseStream.Dispose();

            if (this.ServerResponse != null)
                this.ServerResponse.Close();

        }

        public SessionEventArgs(int bufferSize)
        {
            BUFFER_SIZE = bufferSize;
        }
        public string GetRequestHtmlBody()
        {
            if (RequestHtmlBody == null)
            {

                int bytesRead;
                int totalBytesRead = 0;
                MemoryStream mw = new MemoryStream();
                var buffer = ClientStreamReader.ReadBytes(RequestLength);
                while (totalBytesRead < RequestLength && (bytesRead = buffer.Length) > 0)
                {
                    totalBytesRead += bytesRead;
                    mw.Write(buffer, 0, bytesRead);

                }

                mw.Close();
                RequestHtmlBody = Encoding.Default.GetString(mw.ToArray());
            }
            RequestWasModified = true;
            return RequestHtmlBody;
        }
        public void SetRequestHtmlBody(string body)
        {
            this.RequestHtmlBody = body;
            RequestWasModified = true;
        }
        public string GetResponseHtmlBody()
        {
            if (ResponseHtmlBody == null)
            {

                Encoding = Encoding.GetEncoding(ServerResponse.CharacterSet);


                if (Encoding == null) Encoding = Encoding.Default;
               

                switch (ServerResponse.ContentEncoding)
                {
                    case "gzip":
                        ResponseHtmlBody = CompressionHelper.DecompressGzip(ServerResponseStream, Encoding);
                        break;
                    case "deflate":
                        ResponseHtmlBody = CompressionHelper.DecompressDeflate(ServerResponseStream, Encoding);
                        break;
                    case "zlib":
                        ResponseHtmlBody = CompressionHelper.DecompressZlib(ServerResponseStream, Encoding);
                        break;
                    default:
                        ResponseHtmlBody = DecodeData(ServerResponseStream, Encoding);
                        break;
                }
       
                ResponseWasModified = true;

            }
            return ResponseHtmlBody;
        }

        public void SetResponseHtmlBody(string body)
        {
            this.ResponseHtmlBody = body;
            ResponseWasModified = true;
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