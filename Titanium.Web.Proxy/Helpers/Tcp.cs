using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended.Helpers;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Network.Tcp;

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
        ///     Gets the extended TCP table.
        /// </summary>
        /// <returns>Collection of <see cref="TcpRow" />.</returns>
        internal static TcpTable GetExtendedTcpTable(IpVersion ipVersion)
        {
            var tcpRows = new List<TcpRow>();

            var tcpTable = IntPtr.Zero;
            int tcpTableLength = 0;

            int ipVersionValue = ipVersion == IpVersion.Ipv4 ? NativeMethods.AfInet : NativeMethods.AfInet6;
            int allPid = (int)NativeMethods.TcpTableType.OwnerPidAll;

            if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, ipVersionValue, allPid, 0) != 0)
            {
                try
                {
                    tcpTable = Marshal.AllocHGlobal(tcpTableLength);
                    if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, ipVersionValue, allPid,
                            0) == 0)
                    {
                        var table = (NativeMethods.TcpTable)Marshal.PtrToStructure(tcpTable,
                            typeof(NativeMethods.TcpTable));

                        var rowPtr = (IntPtr)((long)tcpTable + Marshal.SizeOf(table.length));

                        for (int i = 0; i < table.length; ++i)
                        {
                            tcpRows.Add(new TcpRow(
                                (NativeMethods.TcpRow)Marshal.PtrToStructure(rowPtr, typeof(NativeMethods.TcpRow))));
                            rowPtr = (IntPtr)((long)rowPtr + Marshal.SizeOf(typeof(NativeMethods.TcpRow)));
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

            return new TcpTable(tcpRows);
        }

        /// <summary>
        ///     Gets the TCP row by local port number.
        /// </summary>
        /// <returns><see cref="TcpRow" />.</returns>
        internal static TcpRow GetTcpRowByLocalPort(IpVersion ipVersion, int localPort)
        {
            var tcpTable = IntPtr.Zero;
            int tcpTableLength = 0;

            int ipVersionValue = ipVersion == IpVersion.Ipv4 ? NativeMethods.AfInet : NativeMethods.AfInet6;
            int allPid = (int)NativeMethods.TcpTableType.OwnerPidAll;

            if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, ipVersionValue, allPid, 0) != 0)
            {
                try
                {
                    tcpTable = Marshal.AllocHGlobal(tcpTableLength);
                    if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, ipVersionValue, allPid,
                            0) == 0)
                    {
                        var table = (NativeMethods.TcpTable)Marshal.PtrToStructure(tcpTable,
                            typeof(NativeMethods.TcpTable));

                        var rowPtr = (IntPtr)((long)tcpTable + Marshal.SizeOf(table.length));

                        for (int i = 0; i < table.length; ++i)
                        {
                            var tcpRow =
                                (NativeMethods.TcpRow)Marshal.PtrToStructure(rowPtr, typeof(NativeMethods.TcpRow));
                            if (tcpRow.GetLocalPort() == localPort)
                            {
                                return new TcpRow(tcpRow);
                            }

                            rowPtr = (IntPtr)((long)rowPtr + Marshal.SizeOf(typeof(NativeMethods.TcpRow)));
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

            return null;
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
        internal static async Task SendRawApm(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive,
            CancellationTokenSource cancellationTokenSource,
            ExceptionHandler exceptionFunc)
        {
            var taskCompletionSource = new TaskCompletionSource<bool>();
            cancellationTokenSource.Token.Register(() => taskCompletionSource.TrySetResult(true));

            //Now async relay all server=>client & client=>server data
            var clientBuffer = BufferPool.GetBuffer(bufferSize);
            var serverBuffer = BufferPool.GetBuffer(bufferSize);
            try
            {
                BeginRead(clientStream, serverStream, clientBuffer, onDataSend, cancellationTokenSource, exceptionFunc);
                BeginRead(serverStream, clientStream, serverBuffer, onDataReceive, cancellationTokenSource,
                    exceptionFunc);
                await taskCompletionSource.Task;
            }
            finally
            {
                BufferPool.ReturnBuffer(clientBuffer);
                BufferPool.ReturnBuffer(serverBuffer);
            }
        }

        private static void BeginRead(Stream inputStream, Stream outputStream, byte[] buffer,
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
                            BeginRead(inputStream, outputStream, buffer, onCopy, cancellationTokenSource,
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
        private static async Task SendRawTap(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive,
            CancellationTokenSource cancellationTokenSource,
            ExceptionHandler exceptionFunc)
        {
            //Now async relay all server=>client & client=>server data
            var sendRelay =
                clientStream.CopyToAsync(serverStream, onDataSend, bufferSize, cancellationTokenSource.Token);
            var receiveRelay =
                serverStream.CopyToAsync(clientStream, onDataReceive, bufferSize, cancellationTokenSource.Token);

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
        internal static Task SendRaw(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive,
            CancellationTokenSource cancellationTokenSource,
            ExceptionHandler exceptionFunc)
        {
            // todo: fix APM mode
            return SendRawTap(clientStream, serverStream, bufferSize, onDataSend, onDataReceive,
                cancellationTokenSource,
                exceptionFunc);
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
        internal static async Task SendHttp2(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive,
            CancellationTokenSource cancellationTokenSource,
            ExceptionHandler exceptionFunc)
        {
            var connectionId = Guid.NewGuid();

            //Now async relay all server=>client & client=>server data
            var sendRelay =
                CopyHttp2FrameAsync(clientStream, serverStream, onDataSend, bufferSize, connectionId, cancellationTokenSource.Token);
            var receiveRelay =
                CopyHttp2FrameAsync(serverStream, clientStream, onDataReceive, bufferSize, connectionId, cancellationTokenSource.Token);

            await Task.WhenAny(sendRelay, receiveRelay);
            cancellationTokenSource.Cancel();

            await Task.WhenAll(sendRelay, receiveRelay);
        }

        private static async Task CopyHttp2FrameAsync(Stream input, Stream output, Action<byte[], int, int> onCopy,
            int bufferSize, Guid connectionId, CancellationToken cancellationToken)
        {
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
                int streamId = ((headerBuffer[5] & 0x7f) << 24) + (headerBuffer[6] << 16) + (headerBuffer[7] << 8) + headerBuffer[8];

                read = await ForceRead(input, buffer, 0, length, cancellationToken);
                if (read != length)
                {
                    return;
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

        private static async Task<int> ForceRead(Stream input, byte[] buffer, int offset, int bytesToRead, CancellationToken cancellationToken)
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
    }
}
