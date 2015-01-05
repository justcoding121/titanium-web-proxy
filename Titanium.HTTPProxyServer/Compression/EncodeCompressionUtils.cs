using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace HTTPProxyServer
{
    public partial class ProxyServer
    {

        private  void SendNormal(Stream inStream, Stream outStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            int bytesRead;
            while ((bytesRead = inStream.Read(buffer, 0, buffer.Length)) > 0)
            {

                outStream.Write(buffer, 0, bytesRead);

            }
           
        }
        private  void SendChunked(Stream inStream, Stream outStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            var ChunkTrail = Encoding.ASCII.GetBytes(Environment.NewLine);

            int bytesRead;
            while ((bytesRead = inStream.Read(buffer, 0, buffer.Length)) > 0)
            {

                var ChunkHead = Encoding.ASCII.GetBytes(bytesRead.ToString("x2"));
                outStream.Write(ChunkHead, 0, ChunkHead.Length);
                outStream.Write(ChunkTrail, 0, ChunkTrail.Length);
                outStream.Write(buffer, 0, bytesRead);
                outStream.Write(ChunkTrail, 0, ChunkTrail.Length);

            }
            var ChunkEnd = Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);

            outStream.Write(ChunkEnd, 0, ChunkEnd.Length);
        }
        private  void SendChunked(byte[] data, Stream outStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            var ChunkTrail = Encoding.ASCII.GetBytes(Environment.NewLine);



            var ChunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));
            outStream.Write(ChunkHead, 0, ChunkHead.Length);
            outStream.Write(ChunkTrail, 0, ChunkTrail.Length);
            outStream.Write(data, 0, data.Length);
            outStream.Write(ChunkTrail, 0, ChunkTrail.Length);


            var ChunkEnd = Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);

            outStream.Write(ChunkEnd, 0, ChunkEnd.Length);
        }

     
        private  byte[] EncodeData(string ResponseData,  Encoding e)
        {

            return e.GetBytes(ResponseData);
           

        }
        private  byte[] CompressZlib(string ResponseData,  Encoding e)
        {

            Byte[] bytes = e.GetBytes(ResponseData);

            using (MemoryStream ms = new MemoryStream())
            {

                using (Ionic.Zlib.ZlibStream zip = new Ionic.Zlib.ZlibStream(ms, Ionic.Zlib.CompressionMode.Compress, true))
                {
                    zip.Write(bytes, 0, bytes.Length);
                }


                return ms.ToArray();
               


            }
        }

        private  byte[] CompressDeflate(string ResponseData, Encoding e)
        {
            Byte[] bytes = e.GetBytes(ResponseData);

            using (MemoryStream ms = new MemoryStream())
            {

                using (Ionic.Zlib.DeflateStream zip = new Ionic.Zlib.DeflateStream(ms, Ionic.Zlib.CompressionMode.Compress, true))
                {
                    zip.Write(bytes, 0, bytes.Length);
                }

                return ms.ToArray();
              
            }
        }

        private  byte[]  CompressGzip(string ResponseData, Encoding e)
        {
            Byte[] bytes = e.GetBytes(ResponseData);

            using (MemoryStream ms = new MemoryStream())
            {
                using (Ionic.Zlib.GZipStream zip = new Ionic.Zlib.GZipStream(ms, Ionic.Zlib.CompressionMode.Compress, true))
            
                {
                    zip.Write(bytes, 0, bytes.Length);
                }
           
                return  ms.ToArray();
               


            }

        }
        private  void sendData(Stream outStream, byte[] data, bool isChunked)
        {
            if (!isChunked)
            {
                outStream.Write(data, 0, data.Length);
            }
            else
                SendChunked(data, outStream);
        }

       
    }
}
