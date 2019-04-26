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
            var clientSettings = new Http2Settings();
            var serverSettings = new Http2Settings();

            var sessions = new ConcurrentDictionary<int, SessionEventArgs>();

            // Now async relay all server=>client & client=>server data
            var sendRelay =
                copyHttp2FrameAsync(clientStream, serverStream, onDataSend, clientSettings, serverSettings, 
                    sessionFactory, sessions, onBeforeRequest, 
                    bufferSize, connectionId, true, cancellationTokenSource.Token, exceptionFunc);
            var receiveRelay =
                copyHttp2FrameAsync(serverStream, clientStream, onDataReceive, serverSettings, clientSettings, 
                    sessionFactory, sessions, onBeforeResponse, 
                    bufferSize, connectionId, false, cancellationTokenSource.Token, exceptionFunc);

            await Task.WhenAny(sendRelay, receiveRelay);
            cancellationTokenSource.Cancel();

            await Task.WhenAll(sendRelay, receiveRelay);
        }

        private static async Task copyHttp2FrameAsync(Stream input, Stream output, Action<byte[], int, int> onCopy,
            Http2Settings localSettings, Http2Settings remoteSettings,
            Func<SessionEventArgs> sessionFactory, ConcurrentDictionary<int, SessionEventArgs>  sessions, 
            Func<SessionEventArgs, Task> onBeforeRequestResponse,
            int bufferSize, Guid connectionId, bool isClient, CancellationToken cancellationToken,
            ExceptionHandler exceptionFunc)
        {
            int headerTableSize = 0;
            Decoder decoder = null;

            var headerBuffer = new byte[9];
            byte[] buffer = null;
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

                if (buffer == null || buffer.Length < localSettings.MaxFrameSize)
                {
                    buffer = new byte[localSettings.MaxFrameSize];
                }

                read = await forceRead(input, buffer, 0, length, cancellationToken);
                onCopy(buffer, 0, read);
                if (read != length)
                {
                    return;
                }

                bool endStream = false;

                SessionEventArgs args = null;
                RequestResponseBase rr = null;
                if (type == 0 || type == 1)
                {
                    if (!sessions.TryGetValue(streamId, out args))
                    {
                        if (type == 0)
                        {
                            throw new ProxyHttpException("HTTP Body data received before any header frame.", null, args);
                        }

                        if (!isClient)
                        {
                            throw new ProxyHttpException("HTTP Response received before any Request header frame.", null, args);
                        }

                        args = sessionFactory();
                        sessions.TryAdd(streamId, args);
                    }

                    rr = isClient ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;
                }

                //System.Diagnostics.Debug.WriteLine("CONN: " + connectionId + ", CLIENT: " + isClient + ", STREAM: " + streamId + ", TYPE: " + type);
                if (type == 0 /* data */)
                {
                    bool endStreamFlag = (flags & (int)Http2FrameFlag.EndStream) != 0;
                    if (endStreamFlag)
                    {
                        endStream = true;
                    }

                    if (rr.ReadHttp2BodyTaskCompletionSource != null)
                    {
                        // Get body method was called in the "before" event handler

                        var data = rr.Http2BodyData;
                        data.Write(buffer, 0, length);

                        if (endStream)
                        {
                            rr.Body = data.ToArray();
                            rr.IsBodyRead = true;

                            var tcs = rr.ReadHttp2BodyTaskCompletionSource;
                            rr.ReadHttp2BodyTaskCompletionSource = null;

                            if (!tcs.Task.IsCompleted)
                            {
                                tcs.SetResult(true);
                            }

                            rr.Http2BodyData = null;
                        }
                    }   
                }
                else if (type == 1 /* headers */)
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

                    var headerListener = new MyHeaderListener(
                        (name, value) =>
                        {
                            var headers = isClient ? args.HttpClient.Request.Headers : args.HttpClient.Response.Headers;
                            headers.AddHeader(name, value);
                        });
                    try
                    {
                        // recreate the decoder when new value is bigger
                        // should we recreate when smaller, too?
                        if (decoder == null || headerTableSize < localSettings.HeaderTableSize)
                        {
                            headerTableSize = localSettings.HeaderTableSize;
                            decoder = new Decoder(8192, headerTableSize);
                        }

                        decoder.Decode(new BinaryReader(new MemoryStream(buffer, offset, dataLength)),
                            headerListener);
                        decoder.EndHeaderBlock();

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
                        var tcs = new TaskCompletionSource<bool>();
                        rr.ReadHttp2BeforeHandlerTaskCompletionSource = tcs;

                        var handler = onBeforeRequestResponse(args);

                        if (handler == await Task.WhenAny(tcs.Task, handler))
                        {
                            rr.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                            tcs.SetResult(true);
                        }

                        rr.Locked = true;
                    }
                }
                else if (type == 4 /* settings */)
                {
                    if (length % 6 != 0)
                    {
                        // https://httpwg.org/specs/rfc7540.html#SETTINGS
                        // 6.5. SETTINGS
                        // A SETTINGS frame with a length other than a multiple of 6 octets MUST be treated as a connection error (Section 5.4.1) of type FRAME_SIZE_ERROR
                        throw new ProxyHttpException("Invalid settings length", null, null);
                    }

                    int pos = 0;
                    while (pos < length)
                    {
                        int identifier = (buffer[pos++] << 8) + buffer[pos++];
                        int value = (buffer[pos++] << 24) + (buffer[pos++] << 16) + (buffer[pos++] << 8) + buffer[pos++];
                        if (identifier == 1 /*SETTINGS_HEADER_TABLE_SIZE*/)
                        {
                            //System.Diagnostics.Debug.WriteLine("HEADER SIZE CONN: " + connectionId + ", CLIENT: " + isClient + ", value: " + value);
                            remoteSettings.HeaderTableSize = value;
                        }
                        else if (identifier == 5 /*SETTINGS_MAX_FRAME_SIZE*/)
                        {
                            remoteSettings.MaxFrameSize = value;
                        }
                    }
                }

                if (type == 3 /* rst_stream */)
                {
                    int errorCode = (buffer[0] << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3];
                    if (streamId == 0)
                    {
                        // connection error
                        exceptionFunc(new ProxyHttpException("HTTP/2 connection error. Error code: " + errorCode, null, args));
                        return;
                    }
                    else
                    {
                        // stream error
                        sessions.TryRemove(streamId, out _);

                        if (errorCode != 8 /*cancel*/)
                        {
                            exceptionFunc(new ProxyHttpException("HTTP/2 stream error. Error code: " + errorCode, null, args));
                        }
                    }
                }

                if (!isClient && endStream)
                {
                    sessions.TryRemove(streamId, out _);
                    //System.Diagnostics.Debug.WriteLine("REMOVED CONN: " + connectionId + ", CLIENT: " + isClient + ", STREAM: " + streamId + ", TYPE: " + type);
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


        class Http2Settings
        {
            public int HeaderTableSize { get; set; } = 4096;

            public int MaxFrameSize { get; set; } = 16384;
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
