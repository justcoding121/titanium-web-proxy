using System;
using System.Text;
using System.IO;
using System.Net;
using HTTPProxyServer.Utility;

namespace HTTPProxyServer
{
    public class SessionEventArgs : EventArgs
    {
        public string requestURL { get; set; }
        public string hostName { get; set; }
        public CustomBinaryReader clientStreamReader { get; set; }
        public string responseString { get; set; }
        public int requestLength { get; set; }
        public Stream clientStream { get; set; }
        public Version httpVersion { get; set; }
        public bool isAlive { get; set; }
        public bool cancel { get; set; }
        public bool isSecure { get; set; }
        public int port { get; set; }
        private  int BUFFER_SIZE;
        public HttpWebResponse serverResponse { get; set; }
        public Stream serverResponseStream { get; set; }
        public HttpWebRequest proxyRequest { get; set; }
        public Encoding encoding { get; set; }
        public bool wasModified { get; set; }
        public System.Threading.ManualResetEvent finishedRequestEvent { get; set; }
        public string upgradeProtocol { get; set; }

        public SessionEventArgs(int BufferSize)
        {
            BUFFER_SIZE = BufferSize;
        }
        public string decode()
        {

           
            int bytesRead;
            int totalBytesRead = 0;
            MemoryStream mw = new MemoryStream();
            var buffer = clientStreamReader.ReadBytes(requestLength);
            while (totalBytesRead < requestLength && (bytesRead = buffer.Length) > 0)
            {
                totalBytesRead += bytesRead;
                mw.Write(buffer, 0, bytesRead);

            }

            mw.Close();
            return Encoding.Default.GetString(mw.ToArray());
        }
        public void Ok()
        {
            StreamWriter connectStreamWriter = new StreamWriter(clientStream);
            var s = String.Format("HTTP/{0}.{1} {2} {3}", httpVersion.Major, httpVersion.Minor, 200, "Ok");
            connectStreamWriter.WriteLine(s);
            connectStreamWriter.WriteLine(String.Format("Timestamp: {0}", DateTime.Now.ToString()));
            connectStreamWriter.WriteLine("content-length: 0");
            connectStreamWriter.WriteLine("Cache-Control: no-cache, no-store, must-revalidate");
            connectStreamWriter.WriteLine("Pragma: no-cache");
            connectStreamWriter.WriteLine("Expires: 0");

            if (isAlive)
            {
                connectStreamWriter.WriteLine("Connection: Keep-Alive");
            }
            else
                connectStreamWriter.WriteLine("Connection: close");

            connectStreamWriter.WriteLine();
            connectStreamWriter.Flush();
        
            cancel = true;

        }
        public void getResponseBody()
        {
            if (responseString == null)
            {
                
                encoding = Encoding.GetEncoding(serverResponse.CharacterSet);
            

                if (encoding == null) encoding = Encoding.Default;
                string ResponseData = "";

                switch (serverResponse.ContentEncoding)
                {
                    case "gzip":
                        ResponseData = DecompressGzip(serverResponseStream, encoding);
                        break;
                    case "deflate":
                        ResponseData = DecompressDeflate(serverResponseStream, encoding);
                        break;
                    case "zlib":
                        ResponseData = DecompressZlib(serverResponseStream, encoding);
                        break;
                    default:
                        ResponseData = DecodeData(serverResponseStream, encoding);
                        break;
                }
                responseString = ResponseData;
                wasModified = true;
            }
        }
        //stream reader not recomended for images
        private  string DecodeData(Stream responseStream, Encoding e)
        {
            StreamReader reader = new StreamReader(responseStream, e);
            return reader.ReadToEnd();

        }
        private  string DecompressGzip(Stream input, Encoding e)
        {
            using (System.IO.Compression.GZipStream decompressor = new System.IO.Compression.GZipStream(input,System.IO.Compression.CompressionMode.Decompress))
            {

                int read = 0;
                var buffer = new byte[BUFFER_SIZE];

                using (MemoryStream output = new MemoryStream())
                {
                    while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }
                    return e.GetString(output.ToArray());
                }

            }

        }
        private  string DecompressDeflate(Stream input, Encoding e)
        {

            using (Ionic.Zlib.DeflateStream decompressor = new Ionic.Zlib.DeflateStream(input, Ionic.Zlib.CompressionMode.Decompress))
            {
                int read = 0;
                var buffer = new byte[BUFFER_SIZE];

                using (MemoryStream output = new MemoryStream())
                {
                    while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }
                    return e.GetString(output.ToArray());
                }

            }
        }
        private  string DecompressZlib(Stream input, Encoding e)
        {

            using (Ionic.Zlib.ZlibStream decompressor = new Ionic.Zlib.ZlibStream(input, Ionic.Zlib.CompressionMode.Decompress))
            {

                int read = 0;
                var buffer = new byte[BUFFER_SIZE];

                using (MemoryStream output = new MemoryStream())
                {
                    while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }
                    return e.GetString(output.ToArray());
                }
            }

        }





        public IPAddress ipAddress { get; set; }
    }
    
}
