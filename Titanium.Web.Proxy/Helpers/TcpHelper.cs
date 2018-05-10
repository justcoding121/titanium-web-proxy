using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Helpers
{
    internal enum IpVersion
    {
        Ipv4 = 1,
        Ipv6 = 2
    }

    internal class TcpHelper
    {
        /// <summary>
        ///     Gets the process id by local port number.
        /// </summary>
        /// <returns>Process id.</returns>
        internal static unsafe int GetProcessIdByLocalPort(IpVersion ipVersion, int localPort)
        {
            var tcpTable = IntPtr.Zero;
            int tcpTableLength = 0;

            int ipVersionValue = ipVersion == IpVersion.Ipv4 ? NativeMethods.AfInet : NativeMethods.AfInet6;
            const int allPid = (int)NativeMethods.TcpTableType.OwnerPidAll;

            if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, ipVersionValue, allPid, 0) != 0)
            {
                try
                {
                    tcpTable = Marshal.AllocHGlobal(tcpTableLength);
                    if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, ipVersionValue, allPid,
                            0) == 0)
                    {
                        int rowCount = *(int*)tcpTable;
                        uint portInNetworkByteOrder = toNetworkByteOrder((uint)localPort);

                        if (ipVersion == IpVersion.Ipv4)
                        {
                            var rowPtr = (NativeMethods.TcpRow*)(tcpTable + 4);

                            for (int i = 0; i < rowCount; ++i)
                            {
                                if (rowPtr->localPort == portInNetworkByteOrder)
                                {
                                    return rowPtr->owningPid;
                                }

                                rowPtr++;
                            }
                        }
                        else
                        {
                            var rowPtr = (NativeMethods.Tcp6Row*)(tcpTable + 4);

                            for (int i = 0; i < rowCount; ++i)
                            {
                                if (rowPtr->localPort == portInNetworkByteOrder)
                                {
                                    return rowPtr->owningPid;
                                }

                                rowPtr++;
                            }
                        }
                    }
                }
                finally
                {
                    if (tcpTable != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(tcpTable);
                    }
                }
            }

            return 0;
        }

        /// <summary>
        ///     Converts 32-bit integer from native byte order (little-endian)
        ///     to network byte order for port,
        ///     switches 0th and 1st bytes, and 2nd and 3rd bytes
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        private static uint toNetworkByteOrder(uint port)
        {
            return ((port >> 8) & 0x00FF00FFu) | ((port << 8) & 0xFF00FF00u);
        }

        /// <summary>
        ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
        ///     as prefix
        ///     Usefull for websocket requests
        ///     Asynchronous Programming Model, which does not throw exceptions when the socket is closed
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="serverStream"></param>
        /// <param name="bufferSize"></param>
        /// <param name="onDataSend"></param>
        /// <param name="onDataReceive"></param>
        /// <param name="cancellationTokenSource"></param>
        /// <param name="exceptionFunc"></param>
        /// <returns></returns>
        internal static async Task SendRawApm(Stream clientStream, Stream serverStream, 
            IBufferPool bufferPool, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive,
            CancellationTokenSource cancellationTokenSource,
            ExceptionHandler exceptionFunc)
        {
            var taskCompletionSource = new TaskCompletionSource<bool>();
            cancellationTokenSource.Token.Register(() => taskCompletionSource.TrySetResult(true));

            // Now async relay all server=>client & client=>server data
            var clientBuffer = bufferPool.GetBuffer(bufferSize);
            var serverBuffer = bufferPool.GetBuffer(bufferSize);
            try
            {
                beginRead(clientStream, serverStream, clientBuffer, onDataSend, cancellationTokenSource, exceptionFunc);
                beginRead(serverStream, clientStream, serverBuffer, onDataReceive, cancellationTokenSource,
                    exceptionFunc);
                await taskCompletionSource.Task;
            }
            finally
            {
                bufferPool.ReturnBuffer(clientBuffer);
                bufferPool.ReturnBuffer(serverBuffer);
            }
        }

        private static void beginRead(Stream inputStream, Stream outputStream, byte[] buffer,
            Action<byte[], int, int> onCopy, CancellationTokenSource cancellationTokenSource,
            ExceptionHandler exceptionFunc)
        {
            if (cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            bool readFlag = false;
            var readCallback = (AsyncCallback)(ar =>
            {
                if (cancellationTokenSource.IsCancellationRequested || readFlag)
                {
                    return;
                }

                readFlag = true;

                try
                {
                    int read = inputStream.EndRead(ar);
                    if (read <= 0)
                    {
                        cancellationTokenSource.Cancel();
                        return;
                    }

                    onCopy?.Invoke(buffer, 0, read);

                    var writeCallback = (AsyncCallback)(ar2 =>
                    {
                        if (cancellationTokenSource.IsCancellationRequested)
                        {
                            return;
                        }

                        try
                        {
                            outputStream.EndWrite(ar2);
                            beginRead(inputStream, outputStream, buffer, onCopy, cancellationTokenSource,
                                exceptionFunc);
                        }
                        catch (IOException ex)
                        {
                            cancellationTokenSource.Cancel();
                            exceptionFunc(ex);
                        }
                    });

                    outputStream.BeginWrite(buffer, 0, read, writeCallback, null);
                }
                catch (IOException ex)
                {
                    cancellationTokenSource.Cancel();
                    exceptionFunc(ex);
                }
            });

            var readResult = inputStream.BeginRead(buffer, 0, buffer.Length, readCallback, null);
            if (readResult.CompletedSynchronously)
            {
                readCallback(readResult);
            }
        }

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
        /// <param name="exceptionFunc"></param>
        /// <returns></returns>
        private static async Task sendRawTap(Stream clientStream, Stream serverStream,
            IBufferPool bufferPool, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive,
            CancellationTokenSource cancellationTokenSource,
            ExceptionHandler exceptionFunc)
        {
            // Now async relay all server=>client & client=>server data
            var sendRelay =
                clientStream.CopyToAsync(serverStream, onDataSend, bufferPool, bufferSize, cancellationTokenSource.Token);
            var receiveRelay =
                serverStream.CopyToAsync(clientStream, onDataReceive, bufferPool, bufferSize, cancellationTokenSource.Token);

            await Task.WhenAny(sendRelay, receiveRelay);
            cancellationTokenSource.Cancel();

            await Task.WhenAll(sendRelay, receiveRelay);
        }

        /// <summary>
        ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
        ///     as prefix
        ///     Usefull for websocket requests
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="serverStream"></param>
        /// <param name="bufferSize"></param>
        /// <param name="onDataSend"></param>
        /// <param name="onDataReceive"></param>
        /// <param name="cancellationTokenSource"></param>
        /// <param name="exceptionFunc"></param>
        /// <returns></returns>
        internal static Task SendRaw(Stream clientStream, Stream serverStream, 
            IBufferPool bufferPool, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive,
            CancellationTokenSource cancellationTokenSource,
            ExceptionHandler exceptionFunc)
        {
            // todo: fix APM mode
            return sendRawTap(clientStream, serverStream, bufferPool, bufferSize, onDataSend, onDataReceive,
                cancellationTokenSource,
                exceptionFunc);
        }
    }
}
