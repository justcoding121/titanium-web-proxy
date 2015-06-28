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
        public string Hostname { get; set; }
        public CustomBinaryReader ClientStreamReader { get; set; }
        public string ResponseString { get; set; }
        public int RequestLength { get; set; }
        public Stream ClientStream { get; set; }
        public Version HttpVersion { get; set; }
        public bool IsAlive { get; set; }
        public bool Cancel { get; set; }
        public bool IsSecure { get; set; }
        public int Port { get; set; }
        private int BUFFER_SIZE;
        public HttpWebResponse ServerResponse { get; set; }
        public Stream ServerResponseStream { get; set; }
        public HttpWebRequest ProxyRequest { get; set; }
        public Encoding Encoding { get; set; }
        public bool WasModified { get; set; }
        public System.Threading.ManualResetEvent FinishedRequestEvent { get; set; }
        public string UpgradeProtocol { get; set; }

        public SessionEventArgs(int BufferSize)
        {
            BUFFER_SIZE = BufferSize;
        }
        public string Decode()
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
            return Encoding.Default.GetString(mw.ToArray());
        }
        public void Ok(string Html)
        {

            if (Html == null)
                Html = string.Empty;

            var result = Encoding.Default.GetBytes(Html);

            StreamWriter connectStreamWriter = new StreamWriter(ClientStream);
            var s = String.Format("HTTP/{0}.{1} {2} {3}", HttpVersion.Major, HttpVersion.Minor, 200, "Ok");
            connectStreamWriter.WriteLine(s);
            connectStreamWriter.WriteLine(String.Format("Timestamp: {0}", DateTime.Now.ToString()));
            connectStreamWriter.WriteLine("content-length: " + result.Length);
            connectStreamWriter.WriteLine("Cache-Control: no-cache, no-store, must-revalidate");
            connectStreamWriter.WriteLine("Pragma: no-cache");
            connectStreamWriter.WriteLine("Expires: 0");

            if (IsAlive)
            {
                connectStreamWriter.WriteLine("Connection: Keep-Alive");
            }
            else
                connectStreamWriter.WriteLine("Connection: close");

            connectStreamWriter.WriteLine();
            connectStreamWriter.Flush();

            ClientStream.Write(result, 0, result.Length);


            Cancel = true;

        }
        public void GetResponseBody()
        {
            if (ResponseString == null)
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
                ResponseString = ResponseData;
                WasModified = true;
            }
        }
        //stream reader not recomended for images
        private string DecodeData(Stream ResponseStream, Encoding e)
        {
            StreamReader reader = new StreamReader(ResponseStream, e);
            return reader.ReadToEnd();

        }






        public IPAddress ipAddress { get; set; }
    }

}