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
        Ipv6 = 2,
    }

    internal class TcpHelper
    {
        /// <summary>
        /// Gets the extended TCP table.
        /// </summary>
        /// <returns>Collection of <see cref="TcpRow"/>.</returns>
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
                    if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, ipVersionValue, allPid, 0) == 0)
                    {
                        var table = (NativeMethods.TcpTable)Marshal.PtrToStructure(tcpTable, typeof(NativeMethods.TcpTable));

                        var rowPtr = (IntPtr)((long)tcpTable + Marshal.SizeOf(table.length));

                        for (int i = 0; i < table.length; ++i)
                        {
                            tcpRows.Add(new TcpRow((NativeMethods.TcpRow)Marshal.PtrToStructure(rowPtr, typeof(NativeMethods.TcpRow))));
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
        /// Gets the TCP row by local port number.
        /// </summary>
        /// <returns><see cref="TcpRow"/>.</returns>
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
                    if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, ipVersionValue, allPid, 0) == 0)
                    {
                        var table = (NativeMethods.TcpTable)Marshal.PtrToStructure(tcpTable, typeof(NativeMethods.TcpTable));

                        var rowPtr = (IntPtr)((long)tcpTable + Marshal.SizeOf(table.length));

                        for (int i = 0; i < table.length; ++i)
                        {
                            var tcpRow = (NativeMethods.TcpRow)Marshal.PtrToStructure(rowPtr, typeof(NativeMethods.TcpRow));
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
        /// relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers as prefix
        /// Usefull for websocket requests
        /// Asynchronous Programming Model, which does not throw exceptions when the socket is closed
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="serverStream"></param>
        /// <param name="bufferSize"></param>
        /// <param name="onDataSend"></param>
        /// <param name="onDataReceive"></param>
        /// <param name="exceptionFunc"></param>
        /// <returns></returns>
        internal static async Task SendRawApm(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive, ExceptionHandler exceptionFunc)
        {
            var tcs = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource();
            cts.Token.Register(() => tcs.TrySetResult(true));

            //Now async relay all server=>client & client=>server data
            byte[] clientBuffer = BufferPool.GetBuffer(bufferSize);
            byte[] serverBuffer = BufferPool.GetBuffer(bufferSize);
            try
            {
                BeginRead(clientStream, serverStream, clientBuffer, cts, onDataSend, exceptionFunc);
                BeginRead(serverStream, clientStream, serverBuffer, cts, onDataReceive, exceptionFunc);
                await tcs.Task;
            }
            finally
            {
                BufferPool.ReturnBuffer(clientBuffer);
                BufferPool.ReturnBuffer(serverBuffer);
            }
        }

        private static void BeginRead(Stream inputStream, Stream outputStream, byte[] buffer, CancellationTokenSource cts, Action<byte[], int, int> onCopy,
            ExceptionHandler exceptionFunc)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            bool readFlag = false;
            var readCallback = (AsyncCallback)(ar =>
            {
                if (cts.IsCancellationRequested || readFlag)
                {
                    return;
                }

                readFlag = true;

                try
                {
                    int read = inputStream.EndRead(ar);
                    if (read <= 0)
                    {
                        cts.Cancel();
                        return;
                    }

                    onCopy?.Invoke(buffer, 0, read);

                    var writeCallback = (AsyncCallback)(ar2 =>
                    {
                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }

                        try
                        {
                            outputStream.EndWrite(ar2);
                            BeginRead(inputStream, outputStream, buffer, cts, onCopy, exceptionFunc);
                        }
                        catch (IOException ex)
                        {
                            cts.Cancel();
                            exceptionFunc(ex);
                        }
                    });

                    outputStream.BeginWrite(buffer, 0, read, writeCallback, null);
                }
                catch (IOException ex)
                {
                    cts.Cancel();
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
        /// relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers as prefix
        /// Usefull for websocket requests
        /// Task-based Asynchronous Pattern
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="serverStream"></param>
        /// <param name="bufferSize"></param>
        /// <param name="onDataSend"></param>
        /// <param name="onDataReceive"></param>
        /// <param name="exceptionFunc"></param>
        /// <returns></returns>
        internal static async Task SendRawTap(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive, ExceptionHandler exceptionFunc)
        {
            var cts = new CancellationTokenSource();

            //Now async relay all server=>client & client=>server data
            var sendRelay = clientStream.CopyToAsync(serverStream, onDataSend, bufferSize, cts.Token);
            var receiveRelay = serverStream.CopyToAsync(clientStream, onDataReceive, bufferSize, cts.Token);

            await Task.WhenAny(sendRelay, receiveRelay);
            cts.Cancel();

            await Task.WhenAll(sendRelay, receiveRelay);
        }

        /// <summary>
        /// relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers as prefix
        /// Usefull for websocket requests
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="serverStream"></param>
        /// <param name="bufferSize"></param>
        /// <param name="onDataSend"></param>
        /// <param name="onDataReceive"></param>
        /// <param name="exceptionFunc"></param>
        /// <returns></returns>
        internal static Task SendRaw(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive, ExceptionHandler exceptionFunc)
        {
            // todo: fix APM mode
            return SendRawTap(clientStream, serverStream, bufferSize, onDataSend, onDataReceive, exceptionFunc);
        }
    }
}