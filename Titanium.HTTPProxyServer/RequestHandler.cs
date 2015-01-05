using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Net;

namespace HTTPProxyServer
{
   public partial  class ProxyServer
    {
       private  void GetRequestStreamCallback(IAsyncResult asynchronousResult)
       {
           var Args = (SessionEventArgs)asynchronousResult.AsyncState;

           // End the operation
           Stream postStream = Args.proxyRequest.EndGetRequestStream(asynchronousResult);


           if (Args.proxyRequest.ContentLength > 0)
           {
               Args.proxyRequest.AllowWriteStreamBuffering = true;
               try
               {

                   int totalbytesRead = 0;

                   int bytesToRead;
                   if (Args.proxyRequest.ContentLength < BUFFER_SIZE)
                   {
                       bytesToRead = (int)Args.proxyRequest.ContentLength;
                   }
                   else
                       bytesToRead = BUFFER_SIZE;


                   while (totalbytesRead < (int)Args.proxyRequest.ContentLength)
                   {
                       var buffer = Args.clientStreamReader.ReadBytes(bytesToRead);
                       totalbytesRead += buffer.Length;

                       int RemainingBytes = (int)Args.proxyRequest.ContentLength - totalbytesRead;
                       if (RemainingBytes < bytesToRead)
                       {
                           bytesToRead = RemainingBytes;
                       }
                       postStream.Write(buffer, 0, buffer.Length);                      
                   
                   }
                 
                   postStream.Close();
               }
               catch (IOException ex)
               {
                 

                   Args.proxyRequest.KeepAlive = false;
                   Args.finishedRequestEvent.Set();
                   Debug.WriteLine(ex.Message);
                   return;
               }
               catch (WebException ex)
               {
                   

                   Args.proxyRequest.KeepAlive = false;
                   Args.finishedRequestEvent.Set();
                   Debug.WriteLine(ex.Message);
                   return;

               }

           }
           else if (Args.proxyRequest.SendChunked)
           {
               Args.proxyRequest.AllowWriteStreamBuffering = true;
               try
               {
               
                   StringBuilder sb = new StringBuilder();
                   byte[] byteRead = new byte[1];
                   while (true)
                   {

                       Args.clientStream.Read(byteRead, 0, 1);
                       sb.Append(Encoding.ASCII.GetString(byteRead));

                       if (sb.ToString().EndsWith(Environment.NewLine))
                       {
                           var chunkSizeInHex = sb.ToString().Replace(Environment.NewLine, String.Empty);
                           var chunckSize = int.Parse(chunkSizeInHex, System.Globalization.NumberStyles.HexNumber);
                           if (chunckSize == 0)
                           {
                               for (int i = 0; i < Encoding.ASCII.GetByteCount(Environment.NewLine); i++)
                               {
                                   Args.clientStream.ReadByte();
                               }
                               break;
                           }
                           var totalbytesRead = 0;
                           int bytesToRead;
                           if (chunckSize < BUFFER_SIZE)
                           {
                               bytesToRead = chunckSize;
                           }
                           else
                               bytesToRead = BUFFER_SIZE;


                           while (totalbytesRead < chunckSize)
                           {
                               var buffer = Args.clientStreamReader.ReadBytes(bytesToRead);
                               totalbytesRead += buffer.Length;

                               int RemainingBytes = chunckSize - totalbytesRead;
                               if (RemainingBytes < bytesToRead)
                               {
                                   bytesToRead = RemainingBytes;
                               }
                               postStream.Write(buffer, 0, buffer.Length);

                           }

                           for (int i = 0; i < Encoding.ASCII.GetByteCount(Environment.NewLine); i++)
                           {
                               Args.clientStream.ReadByte();
                           }
                           sb.Clear();
                       }

                   }
                   postStream.Close();
               }
               catch (IOException ex)
               {
                   if (postStream != null)
                       postStream.Close();

                   Args.proxyRequest.KeepAlive = false;
                   Args.finishedRequestEvent.Set();
                   Debug.WriteLine(ex.Message);
                   return;
               }
               catch (WebException ex)
               {
                   if (postStream != null)
                       postStream.Close();

                   Args.proxyRequest.KeepAlive = false;
                   Args.finishedRequestEvent.Set();
                   Debug.WriteLine(ex.Message);
                   return;

               }
           }
       
           Args.proxyRequest.BeginGetResponse(new AsyncCallback(GetResponseCallback), Args);
       
       }

    }
}
