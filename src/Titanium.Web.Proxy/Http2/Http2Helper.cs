#if NETCOREAPP2_1
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http2.Hpack;

namespace Titanium.Web.Proxy.Http2
{
    [Flags]
    internal enum Http2FrameFlag
    {
        Ack = 0x01,
        EndStream = 0x01,
        EndHeaders = 0x04,
        Padded = 0x08,
        Priority = 0x20,
    }

    internal class Http2Helper
    {
        /// <summary>
        ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
        ///     as prefix
        ///     Usefull for websocket requests
        ///     Task-based Asynchronous Pattern
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="serverStream"></param>
        /// <param name="bufferSize"></param>
        /// <param name="onDataSend"></param>
        /// <param name="onDataReceive"></param>
        /// <param name="cancellationTokenSource"></param>
        /// <param name="connectionId"></param>
        /// <param name="exceptionFunc"></param>
        /// <returns></returns>
        internal static async Task SendHttp2(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive,
            CancellationTokenSource cancellationTokenSource, Guid connectionId,
            ExceptionHandler exceptionFunc)
        {
            // Now async relay all server=>client & client=>server data
            var sendRelay =
                CopyHttp2FrameAsync(clientStream, serverStream, onDataSend, bufferSize, connectionId,
                    true, cancellationTokenSource.Token);
            var receiveRelay =
                CopyHttp2FrameAsync(serverStream, clientStream, onDataReceive, bufferSize, connectionId,
                    false, cancellationTokenSource.Token);

            await Task.WhenAny(sendRelay, receiveRelay);
            cancellationTokenSource.Cancel();

            await Task.WhenAll(sendRelay, receiveRelay);
        }

        private static async Task CopyHttp2FrameAsync(Stream input, Stream output, Action<byte[], int, int> onCopy,
            int bufferSize, Guid connectionId, bool isClient, CancellationToken cancellationToken)
        {
            var decoder = new Decoder(8192, 4096);

            var headerBuffer = new byte[9];
            var buffer = new byte[32768];
            while (true)
            {
                int read = await ForceRead(input, headerBuffer, 0, 9, cancellationToken);
                if (read != 9)
                {
                    return;
                }

                int length = (headerBuffer[0] << 16) + (headerBuffer[1] << 8) + headerBuffer[2];
                byte type = headerBuffer[3];
                byte flags = headerBuffer[4];
                int streamId = ((headerBuffer[5] & 0x7f) << 24) + (headerBuffer[6] << 16) + (headerBuffer[7] << 8) +
                               headerBuffer[8];

                read = await ForceRead(input, buffer, 0, length, cancellationToken);
                if (read != length)
                {
                    return;
                }

                if (isClient)
                {
                    if (type == 1 /*headers*/)
                    {
                        bool endHeaders = (flags & (int)Http2FrameFlag.EndHeaders) != 0;
                        bool padded = (flags & (int)Http2FrameFlag.Padded) != 0;
                        bool priority = (flags & (int)Http2FrameFlag.Priority) != 0;

                        System.Diagnostics.Debug.WriteLine("HEADER: " + streamId + " end: " + endHeaders);

                        int offset = 0;
                        if (padded)
                        {
                            offset = 1;
                        }

                        if (priority)
                        {
                            offset += 5;
                        }

                        int dataLength = length - offset;
                        if (padded)
                        {
                            dataLength -= buffer[0];
                        }

                        var headerListener = new MyHeaderListener();
                        try
                        {
                            decoder.Decode(new BinaryReader(new MemoryStream(buffer, offset, dataLength)),
                                headerListener);
                            decoder.EndHeaderBlock();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }

                await output.WriteAsync(headerBuffer, 0, headerBuffer.Length, cancellationToken);
                await output.WriteAsync(buffer, 0, length, cancellationToken);

                /*using (var fs = new System.IO.FileStream($@"c:\11\{connectionId}.{streamId}.dat", FileMode.Append))
                {
                    fs.Write(headerBuffer, 0, headerBuffer.Length);
                    fs.Write(buffer, 0, length);
                }*/
            }
        }

        private static async Task<int> ForceRead(Stream input, byte[] buffer, int offset, int bytesToRead,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (bytesToRead > 0)
            {
                int read = await input.ReadAsync(buffer, offset, bytesToRead, cancellationToken);
                if (read == -1)
                {
                    break;
                }

                totalRead += read;
                bytesToRead -= read;
                offset += read;
            }

            return totalRead;
        }

        class MyHeaderListener : IHeaderListener
        {
            public void AddHeader(string name, string value, bool sensitive)
            {
                Console.WriteLine(name + ": " + value + " " + sensitive);
            }
        }
    }
}
#endif