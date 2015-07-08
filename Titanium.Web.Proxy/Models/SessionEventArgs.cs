using System;
using System.Text;
using System.IO;
using System.Net;
using Titanium.Web.Proxy.Helpers;


namespace Titanium.Web.Proxy.Models
{
    public class SessionEventArgs : EventArgs
    {
       
        public string RequestURL { get; set; }
        public string RequestHostname { get; set; }
        
        public bool IsSSLRequest { get; set; }

        public int ClientPort { get; set; }
        public IPAddress ClientIpAddress { get; set; }


        public HttpWebRequest ProxyRequest { get; set; }
        public HttpWebResponse ServerResponse { get; set; }
        

        internal int BUFFER_SIZE;

        internal int RequestLength { get; set; }
        internal Version RequestHttpVersion { get; set; }
        internal bool RequestIsAlive { get; set; }
        internal bool CancelRequest { get; set; }
        internal CustomBinaryReader ClientStreamReader { get; set; }
        internal Stream ClientStream { get; set; }
        internal Stream ServerResponseStream { get; set; }
        internal Encoding Encoding { get; set; }
        internal bool WasModified { get; set; }
        internal System.Threading.ManualResetEvent FinishedRequestEvent { get; set; }
        internal string UpgradeProtocol { get; set; }


        internal string RequestHtmlBody { get; set; }
        internal string ResponseHtmlBody { get; set; }

        public SessionEventArgs(int BufferSize)
        {
            BUFFER_SIZE = BufferSize;
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
            return RequestHtmlBody;
        }
        public void SetRequestHtmlBody(string Body)
        {
            this.RequestHtmlBody = Body;
        }
        public string GetResponseHtmlBody()
        {
            if (ResponseHtmlBody == null)
            {

                Encoding = Encoding.GetEncoding(ServerResponse.CharacterSet);


                if (Encoding == null) Encoding = Encoding.Default;
                string ResponseData = "";

                switch (ServerResponse.ContentEncoding)
                {
                    case "gzip":
                        ResponseData = CompressionHelper.DecompressGzip(ServerResponseStream, Encoding);
                        break;
                    case "deflate":
                        ResponseData = CompressionHelper.DecompressDeflate(ServerResponseStream, Encoding);
                        break;
                    case "zlib":
                        ResponseData = CompressionHelper.DecompressZlib(ServerResponseStream, Encoding);
                        break;
                    default:
                        ResponseData = DecodeData(ServerResponseStream, Encoding);
                        break;
                }
                ResponseHtmlBody = ResponseData;
                WasModified = true;
              
            }
            return ResponseHtmlBody;
        }

        public void SetResponseHtmlBody(string Body)
        {
            this.ResponseHtmlBody = Body;
        }
        //stream reader not recomended for images
        private string DecodeData(Stream ResponseStream, Encoding e)
        {
            StreamReader reader = new StreamReader(ResponseStream, e);
            return reader.ReadToEnd();

        }

        public void Ok(string Html)
        {

            if (Html == null)
                Html = string.Empty;

            var result = Encoding.Default.GetBytes(Html);

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