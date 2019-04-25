#if NETCOREAPP2_1
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http;
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
        ///     Useful for websocket requests
        ///     Task-based Asynchronous Pattern
        /// </summary>
        /// <returns></returns>
        internal static async Task SendHttp2(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive,
            Func<SessionEventArgs> sessionFactory,
            Func<SessionEventArgs, Task> onBeforeRequest, Func<SessionEventArgs, Task> onBeforeResponse,
            CancellationTokenSource cancellationTokenSource, Guid connectionId,
            ExceptionHandler exceptionFunc)
        {
            var decoder = new Decoder(8192, 4096 * 16);
            var sessions = new ConcurrentDictionary<int, SessionEventArgs>();

            // Now async relay all server=>client & client=>server data
            var sendRelay =
                copyHttp2FrameAsync(clientStream, serverStream, onDataSend, sessionFactory, decoder, sessions, onBeforeRequest, 
                    bufferSize, connectionId, true, cancellationTokenSource.Token, exceptionFunc);
            var receiveRelay =
                copyHttp2FrameAsync(serverStream, clientStream, onDataReceive, sessionFactory, decoder, sessions, onBeforeResponse, 
                    bufferSize, connectionId, false, cancellationTokenSource.Token, exceptionFunc);

            await Task.WhenAny(sendRelay, receiveRelay);
            cancellationTokenSource.Cancel();

            await Task.WhenAll(sendRelay, receiveRelay);
        }

        private static async Task copyHttp2FrameAsync(Stream input, Stream output, Action<byte[], int, int> onCopy,
            Func<SessionEventArgs> sessionFactory, Decoder decoder, ConcurrentDictionary<int, SessionEventArgs>  sessions, 
            Func<SessionEventArgs, Task> onBeforeRequestResponse,
            int bufferSize, Guid connectionId, bool isClient, CancellationToken cancellationToken,
            ExceptionHandler exceptionFunc)
        {
            decoder = new Decoder(8192, 4096 * 16);

            var headerBuffer = new byte[9];
            var buffer = new byte[32768];
            while (true)
            {
                int read = await forceRead(input, headerBuffer, 0, 9, cancellationToken);
                onCopy(headerBuffer, 0, read);
                if (read != 9)
                {
                    return;
                }

                int length = (headerBuffer[0] << 16) + (headerBuffer[1] << 8) + headerBuffer[2];
                byte type = headerBuffer[3];
                byte flags = headerBuffer[4];
                int streamId = ((headerBuffer[5] & 0x7f) << 24) + (headerBuffer[6] << 16) + (headerBuffer[7] << 8) +
                               headerBuffer[8];

                read = await forceRead(input, buffer, 0, length, cancellationToken);
                onCopy(buffer, 0, read);
                if (read != length)
                {
                    return;
                }

                bool endStream = false;

                //System.Diagnostics.Debug.WriteLine("CLIENT: " + isClient + ", STREAM: " + streamId + ", TYPE: " + type);
                if (type == 0 /* data */)
                {
                    bool endStreamFlag = (flags & (int)Http2FrameFlag.EndStream) != 0;
                    if (endStreamFlag)
                    {
                        endStream = true;
                    }

                    if (!sessions.TryGetValue(streamId, out var args))
                    {
                        throw new ProxyHttpException("HTTP Body data received before any header frame.", null, args);
                    }

                    var rr = isClient ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;
                    if (rr.ReadHttp2BodyTaskCompletionSource != null)
                    {
                        // Get body method was called in the "before" event handler

                        var data = rr.Http2BodyData;
                        data.Write(buffer, 0, length);

                        if (endStream)
                        {
                            rr.Body = data.ToArray();
                            rr.IsBodyRead = true;
                            rr.ReadHttp2BodyTaskCompletionSource.SetResult(true);

                            rr.ReadHttp2BodyTaskCompletionSource = null;
                            rr.Http2BodyData = null;
                        }
                    }
                }
                else if (type == 1 /*headers*/)
                {
                    bool endHeaders = (flags & (int)Http2FrameFlag.EndHeaders) != 0;
                    bool padded = (flags & (int)Http2FrameFlag.Padded) != 0;
                    bool priority = (flags & (int)Http2FrameFlag.Priority) != 0;
                    bool endStreamFlag = (flags & (int)Http2FrameFlag.EndStream) != 0;
                    if (endStreamFlag)
                    {
                        endStream = true;
                    }

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

                    if (!sessions.TryGetValue(streamId, out var args))
                    {
                        args = sessionFactory();
                        sessions.TryAdd(streamId, args);
                    }

                    var headerListener = new MyHeaderListener(
                        (name, value) =>
                        {
                            var headers = isClient ? args.HttpClient.Request.Headers : args.HttpClient.Response.Headers;
                            headers.AddHeader(name, value);
                        });
                    try
                    {
                        lock (decoder)
                        {
                            decoder.Decode(new BinaryReader(new MemoryStream(buffer, offset, dataLength)),
                                headerListener);
                            decoder.EndHeaderBlock();
                        }

                        if (isClient)
                        {
                            var request = args.HttpClient.Request;
                            request.HttpVersion = HttpVersion.Version20;
                            request.Method = headerListener.Method;
                            request.OriginalUrl = headerListener.Status;
                            request.RequestUri = headerListener.GetUri();
                        }
                        else
                        {
                            var response = args.HttpClient.Response;
                            response.HttpVersion = HttpVersion.Version20;
                            int.TryParse(headerListener.Status, out int statusCode);
                            response.StatusCode = statusCode;
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptionFunc(new ProxyHttpException("Failed to decode HTTP/2 headers", ex, args));
                    }

                    if (endHeaders)
                    {
                        var handler = onBeforeRequestResponse(args);

                        var tcs = new TaskCompletionSource<bool>();
                        args.ReadHttp2BodyTaskCompletionSource = tcs;

                        if (handler == await Task.WhenAny(handler, tcs.Task))
                        {
                            tcs.SetResult(true);
                        }

                        var rr = isClient ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;
                        rr.Locked = true;
                    }
                }

                if (!isClient && endStream)
                {
                    sessions.TryRemove(streamId, out _);
                }

                // do not cancel the write operation
                await output.WriteAsync(headerBuffer, 0, headerBuffer.Length/*, cancellationToken*/);
                await output.WriteAsync(buffer, 0, length/*, cancellationToken*/);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                /*using (var fs = new System.IO.FileStream($@"c:\temp\{connectionId}.{streamId}.dat", FileMode.Append))
                {
                    fs.Write(headerBuffer, 0, headerBuffer.Length);
                    fs.Write(buffer, 0, length);
                }*/
            }
        }

        private static async Task<int> forceRead(Stream input, byte[] buffer, int offset, int bytesToRead,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (bytesToRead > 0)
            {
                int read = await input.ReadAsync(buffer, offset, bytesToRead, cancellationToken);
                if (read == 0)
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
            private readonly Action<string, string> addHeaderFunc;

            public string Method { get; private set; }

            public string Status { get; private set; }

            private string authority;

            private string scheme;

            public string Path { get; private set; }

            public MyHeaderListener(Action<string, string> addHeaderFunc)
            {
                this.addHeaderFunc = addHeaderFunc;
            }

            public void AddHeader(string name, string value, bool sensitive)
            {
                if (name[0] == ':')
                {
                    switch (name)
                    {
                        case ":method":
                            Method = value;
                            return;
                        case ":authority":
                            authority = value;
                            return;
                        case ":scheme":
                            scheme = value;
                            return;
                        case ":path":
                            Path = value;
                            return;
                        case ":status":
                            Status = value;
                            return;
                    }
                }

                addHeaderFunc(name, value);
            }

            public Uri GetUri()
            {
                if (authority == null)
                {
                    // todo
                    authority = "abc.abc";
                }

                return new Uri(scheme + "://" + authority + Path);
            }
        }
    }
}
#endif
