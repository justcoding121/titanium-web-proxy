using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers;

internal class HttpStream : Stream, IHttpStreamWriter, IHttpStreamReader, IPeekStream
{
    private readonly bool leaveOpen;
    private readonly byte[] streamBuffer;

    private static Encoding Encoding => HttpHeader.Encoding;

    private static readonly bool networkStreamHack = true;

    private int bufferPos;

    private bool disposed;

    private bool closedWrite;

    private readonly IBufferPool bufferPool;
    private readonly CancellationToken cancellationToken;

    public bool IsNetworkStream { get; }

    public event EventHandler<DataEventArgs>? DataRead;

    public event EventHandler<DataEventArgs>? DataWrite;

    private Stream BaseStream { get; }

    public bool IsClosed { get; private set; }

    static HttpStream()
    {
        // TODO: remove this hack when removing .NET 4.x support
        try
        {
            var method = typeof(NetworkStream).GetMethod(nameof(Stream.ReadAsync),
                new[] { typeof(byte[]), typeof(int), typeof(int), typeof(CancellationToken) });
            if (method != null && method.DeclaringType != typeof(Stream)) networkStreamHack = false;
        }
        catch
        {
            // ignore
        }
    }

    private static readonly byte[] newLine = ProxyConstants.NewLineBytes;
    private readonly ProxyServer server;

    /// <summary>
    ///     Initializes a new instance of the <see cref="HttpStream" /> class.
    /// </summary>
    /// <param name="baseStream">The base stream.</param>
    /// <param name="bufferPool">Bufferpool.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="leaveOpen">
    ///     <see langword="true" /> to leave the stream open after disposing the
    ///     <see cref="T:CustomBufferedStream" /> object; otherwise, <see langword="false" />.
    /// </param>
    internal HttpStream(ProxyServer server, Stream baseStream, IBufferPool bufferPool,
        CancellationToken cancellationToken, bool leaveOpen = false)
    {
        this.server = server;

        if (baseStream is NetworkStream) IsNetworkStream = true;

        BaseStream = baseStream;
        this.leaveOpen = leaveOpen;
        streamBuffer = bufferPool.GetBuffer();
        this.bufferPool = bufferPool;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>
    ///     When overridden in a derived class, clears all buffers for this stream and causes any buffered data to be written
    ///     to the underlying device.
    /// </summary>
    public override void Flush()
    {
        if (closedWrite) return;

        try
        {
            BaseStream.Flush();
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    /// <summary>
    ///     When overridden in a derived class, sets the position within the current stream.
    /// </summary>
    /// <param name="offset">A byte offset relative to the <paramref name="origin" /> parameter.</param>
    /// <param name="origin">
    ///     A value of type <see cref="T:System.IO.SeekOrigin" /> indicating the reference point used to
    ///     obtain the new position.
    /// </param>
    /// <returns>
    ///     The new position within the current stream.
    /// </returns>
    public override long Seek(long offset, SeekOrigin origin)
    {
        Available = 0;
        bufferPos = 0;
        return BaseStream.Seek(offset, origin);
    }

    /// <summary>
    ///     When overridden in a derived class, sets the length of the current stream.
    /// </summary>
    /// <param name="value">The desired length of the current stream in bytes.</param>
    public override void SetLength(long value)
    {
        BaseStream.SetLength(value);
    }

    /// <summary>
    ///     When overridden in a derived class, reads a sequence of bytes from the current stream and advances the position
    ///     within the stream by the number of bytes read.
    /// </summary>
    /// <param name="buffer">
    ///     An array of bytes. When this method returns, the buffer contains the specified byte array with the
    ///     values between <paramref name="offset" /> and (<paramref name="offset" /> + <paramref name="count" /> - 1) replaced
    ///     by the bytes read from the current source.
    /// </param>
    /// <param name="offset">
    ///     The zero-based byte offset in <paramref name="buffer" /> at which to begin storing the data read
    ///     from the current stream.
    /// </param>
    /// <param name="count">The maximum number of bytes to be read from the current stream.</param>
    /// <returns>
    ///     The total number of bytes read into the buffer. This can be less than the number of bytes requested if that many
    ///     bytes are not currently available, or zero (0) if the end of the stream has been reached.
    /// </returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (Available == 0) FillBuffer();

        var available = Math.Min(Available, count);
        if (available > 0)
        {
            Buffer.BlockCopy(streamBuffer, bufferPos, buffer, offset, available);
            bufferPos += available;
            Available -= available;
        }

        return available;
    }

    /// <summary>
    ///     When overridden in a derived class, writes a sequence of bytes to the current stream and advances the current
    ///     position within this stream by the number of bytes written.
    /// </summary>
    /// <param name="buffer">An array of bytes. This method copies count bytes from buffer to the current stream.</param>
    /// <param name="offset">The zero-based byte offset in buffer at which to begin copying bytes to the current stream.</param>
    /// <param name="count">The number of bytes to be written to the current stream.</param>
    [DebuggerStepThrough]
    public override void Write(byte[] buffer, int offset, int count)
    {
        OnDataWrite(buffer, offset, count);

        if (closedWrite) return;

        try
        {
            BaseStream.Write(buffer, offset, count);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    /// <summary>
    ///     Asynchronously reads the bytes from the current stream and writes them to another stream, using a specified buffer
    ///     size and cancellation token.
    /// </summary>
    /// <param name="destination">The stream to which the contents of the current stream will be copied.</param>
    /// <param name="bufferSize">
    ///     The size, in bytes, of the buffer. This value must be greater than zero. The default size is
    ///     81920.
    /// </param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous copy operation.
    /// </returns>
    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        if (Available > 0)
        {
            await destination.WriteAsync(streamBuffer, bufferPos, Available, cancellationToken);

            Available = 0;
        }

        await base.CopyToAsync(destination, bufferSize, cancellationToken);
    }

    /// <summary>
    ///     Asynchronously clears all buffers for this stream, causes any buffered data to be written to the underlying device,
    ///     and monitors cancellation requests.
    /// </summary>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous flush operation.
    /// </returns>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (closedWrite) return;

        try
        {
            await BaseStream.FlushAsync(cancellationToken);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    /// <summary>
    ///     Asynchronously reads a sequence of bytes from the current stream,
    ///     advances the position within the stream by the number of bytes read,
    ///     and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write the data into.</param>
    /// <param name="offset">
    ///     The byte offset in <paramref name="buffer" /> at which
    ///     to begin writing data from the stream.
    /// </param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests.
    ///     The default value is <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous read operation.
    ///     The value of the parameter contains the total
    ///     number of bytes read into the buffer.
    ///     The result value can be less than the number of bytes
    ///     requested if the number of bytes currently available is
    ///     less than the requested number, or it can be 0 (zero)
    ///     if the end of the stream has been reached.
    /// </returns>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (Available == 0) await FillBufferAsync(cancellationToken);

        var available = Math.Min(Available, count);
        if (available > 0)
        {
            Buffer.BlockCopy(streamBuffer, bufferPos, buffer, offset, available);
            bufferPos += available;
            Available -= available;
        }

        return available;
    }

    /// <summary>
    ///     Asynchronously reads a sequence of bytes from the current stream,
    ///     advances the position within the stream by the number of bytes read,
    ///     and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write the data into.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests.
    ///     The default value is <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous read operation.
    ///     The value of the parameter contains the total
    ///     number of bytes read into the buffer.
    ///     The result value can be less than the number of bytes
    ///     requested if the number of bytes currently available is
    ///     less than the requested number, or it can be 0 (zero)
    ///     if the end of the stream has been reached.
    /// </returns>
#if NETSTANDARD2_1
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken =
 default)
#else
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
#endif
    {
        if (Available == 0) await FillBufferAsync(cancellationToken);

        var available = Math.Min(Available, buffer.Length);
        if (available > 0)
        {
            new Span<byte>(streamBuffer, bufferPos, available).CopyTo(buffer.Span);
            bufferPos += available;
            Available -= available;
        }

        return available;
    }

    /// <summary>
    ///     Reads a byte from the stream and advances the position within the stream by one byte, or returns -1 if at the end
    ///     of the stream.
    /// </summary>
    /// <returns>
    ///     The unsigned byte cast to an Int32, or -1 if at the end of the stream.
    /// </returns>
    public override int ReadByte()
    {
        if (Available == 0) FillBuffer();

        if (Available == 0) return -1;

        Available--;
        return streamBuffer[bufferPos++];
    }

    /// <summary>
    ///     Peeks a byte asynchronous.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public async ValueTask<int> PeekByteAsync(int index, CancellationToken cancellationToken = default)
    {
        // When index is greater than the buffer size
        if (streamBuffer.Length <= index)
            throw new Exception("Requested Peek index exceeds the buffer size. Consider increasing the buffer size.");

        while (Available <= index)
        {
            // When index is greater than the buffer size
            var fillResult = await FillBufferAsync(cancellationToken);
            if (!fillResult) return -1;
        }

        return streamBuffer[bufferPos + index];
    }

    /// <summary>
    ///     Peeks bytes asynchronous.
    /// </summary>
    /// <param name="buffer">The buffer to copy.</param>
    /// <param name="offset">The offset where copying.</param>
    /// <param name="index">The index.</param>
    /// <param name="count">The count.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public async ValueTask<int> PeekBytesAsync(byte[] buffer, int offset, int index, int count,
        CancellationToken cancellationToken = default)
    {
        // When index is greater than the buffer size
        if (streamBuffer.Length <= index + count)
            throw new Exception(
                "Requested Peek index and size exceeds the buffer size. Consider increasing the buffer size.");

        while (Available <= index)
        {
            var fillResult = await FillBufferAsync(cancellationToken);
            if (!fillResult) return 0;
        }

        if (Available - index < count) count = Available - index;

        Buffer.BlockCopy(streamBuffer, index, buffer, offset, count);
        return count;
    }

    /// <summary>
    ///     Peeks a byte from buffer.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <returns></returns>
    /// <exception cref="Exception">Index is out of buffer size</exception>
    public byte PeekByteFromBuffer(int index)
    {
        if (Available <= index) throw new Exception("Index is out of buffer size");

        return streamBuffer[bufferPos + index];
    }

    /// <summary>
    ///     Reads a byte from buffer.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="Exception">Buffer is empty</exception>
    public byte ReadByteFromBuffer()
    {
        if (Available == 0) throw new Exception("Buffer is empty");

        Available--;
        return streamBuffer[bufferPos++];
    }

    /// <summary>
    ///     Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream
    ///     by the number of bytes written, and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write data from.</param>
    /// <param name="offset">The zero-based byte offset in buffer from which to begin copying bytes to the stream.</param>
    /// <param name="count">The maximum number of bytes to write.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None"></see>.
    /// </param>
    [DebuggerStepThrough]
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        OnDataWrite(buffer, offset, count);

        if (closedWrite) return;

        try
        {
            await BaseStream.WriteAsync(buffer, offset, count, cancellationToken);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    /// <summary>
    ///     Writes a byte to the current position in the stream and advances the position within the stream by one byte.
    /// </summary>
    /// <param name="value">The byte to write to the stream.</param>
    public override void WriteByte(byte value)
    {
        if (closedWrite) return;

        var buffer = bufferPool.GetBuffer();
        try
        {
            buffer[0] = value;
            OnDataWrite(buffer, 0, 1);
            BaseStream.Write(buffer, 0, 1);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    protected virtual void OnDataWrite(byte[] buffer, int offset, int count)
    {
        DataWrite?.Invoke(this, new DataEventArgs(buffer, offset, count));
    }

    protected virtual void OnDataRead(byte[] buffer, int offset, int count)
    {
        DataRead?.Invoke(this, new DataEventArgs(buffer, offset, count));
    }

    /// <summary>
    ///     Releases the unmanaged resources used by the <see cref="T:System.IO.Stream" /> and optionally releases the managed
    ///     resources.
    /// </summary>
    /// <param name="disposing">
    ///     true to release both managed and unmanaged resources; false to release only unmanaged
    ///     resources.
    /// </param>
    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            IsClosed = true;
            closedWrite = true;

            if (disposing)
            {
                if (!leaveOpen) BaseStream.Dispose();

                bufferPool.ReturnBuffer(streamBuffer);
            }
        }
    }

    /// <summary>
    ///     When overridden in a derived class, gets a value indicating whether the current stream supports reading.
    /// </summary>
    public override bool CanRead => BaseStream.CanRead;

    /// <summary>
    ///     When overridden in a derived class, gets a value indicating whether the current stream supports seeking.
    /// </summary>
    public override bool CanSeek => BaseStream.CanSeek;

    /// <summary>
    ///     When overridden in a derived class, gets a value indicating whether the current stream supports writing.
    /// </summary>
    public override bool CanWrite => BaseStream.CanWrite;

    /// <summary>
    ///     Gets a value that determines whether the current stream can time out.
    /// </summary>
    public override bool CanTimeout => BaseStream.CanTimeout;

    /// <summary>
    ///     When overridden in a derived class, gets the length in bytes of the stream.
    /// </summary>
    public override long Length => BaseStream.Length;

    /// <summary>
    ///     Gets a value indicating whether data is available.
    /// </summary>
    public bool DataAvailable => Available > 0;

    /// <summary>
    ///     Gets the available data size.
    /// </summary>
    public int Available { get; private set; }

    /// <summary>
    ///     When overridden in a derived class, gets or sets the position within the current stream.
    /// </summary>
    public override long Position
    {
        get => BaseStream.Position;
        set => BaseStream.Position = value;
    }

    /// <summary>
    ///     Gets or sets a value, in miliseconds, that determines how long the stream will attempt to read before timing out.
    /// </summary>
    public override int ReadTimeout
    {
        get => BaseStream.ReadTimeout;
        set => BaseStream.ReadTimeout = value;
    }

    /// <summary>
    ///     Gets or sets a value, in miliseconds, that determines how long the stream will attempt to write before timing out.
    /// </summary>
    public override int WriteTimeout
    {
        get => BaseStream.WriteTimeout;
        set => BaseStream.WriteTimeout = value;
    }

    /// <summary>
    ///     Fills the buffer.
    /// </summary>
    public bool FillBuffer()
    {
        if (IsClosed) throw new Exception("Stream is already closed");

        if (Available > 0)
            // normally we fill the buffer only when it is empty, but sometimes we need more data
            // move the remaining data to the beginning of the buffer 
            Buffer.BlockCopy(streamBuffer, bufferPos, streamBuffer, 0, Available);

        bufferPos = 0;

        var result = false;
        try
        {
            var readBytes = BaseStream.Read(streamBuffer, Available, streamBuffer.Length - Available);
            result = readBytes > 0;
            if (result)
            {
                OnDataRead(streamBuffer, Available, readBytes);
                Available += readBytes;
            }
        }
        catch
        {
            if (!IsNetworkStream)
                throw;
        }
        finally
        {
            if (!result)
            {
                IsClosed = true;
                closedWrite = true;
            }
        }

        return result;
    }

    /// <summary>
    ///     Fills the buffer asynchronous.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public async ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken = default)
    {
        if (IsClosed) throw new Exception("Stream is already closed");

        var bytesToRead = streamBuffer.Length - Available;
        if (bytesToRead == 0) return false;

        if (Available > 0)
            // normally we fill the buffer only when it is empty, but sometimes we need more data
            // move the remaining data to the beginning of the buffer 
            Buffer.BlockCopy(streamBuffer, bufferPos, streamBuffer, 0, Available);

        bufferPos = 0;

        var result = false;
        try
        {
            var readTask = BaseStream.ReadAsync(streamBuffer, Available, bytesToRead, cancellationToken);
            if (IsNetworkStream) readTask = readTask.WithCancellation(cancellationToken);

            var readBytes = await readTask;
            result = readBytes > 0;
            if (result)
            {
                OnDataRead(streamBuffer, Available, readBytes);
                Available += readBytes;
            }
        }
        catch
        {
            if (!IsNetworkStream)
                throw;
        }
        finally
        {
            if (!result)
            {
                IsClosed = true;
                closedWrite = true;
            }
        }

        return result;
    }

    /// <summary>
    ///     Read a line from the byte stream
    /// </summary>
    /// <returns></returns>
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        return ReadLineInternalAsync(this, bufferPool, cancellationToken);
    }

    /// <summary>
    ///     Read a line from the byte stream
    /// </summary>
    /// <returns></returns>
    internal static async ValueTask<string?> ReadLineInternalAsync(ILineStream reader, IBufferPool bufferPool,
        CancellationToken cancellationToken = default)
    {
        byte lastChar = default;

        var bufferDataLength = 0;

        // try to use buffer from the buffer pool, usually it is enough
        var bufferPoolBuffer = bufferPool.GetBuffer();
        var buffer = bufferPoolBuffer;

        try
        {
            while (reader.DataAvailable || await reader.FillBufferAsync(cancellationToken))
            {
                var newChar = reader.ReadByteFromBuffer();
                buffer[bufferDataLength] = newChar;

                // if new line
                if (newChar == '\n')
                {
                    if (lastChar == '\r') return Encoding.GetString(buffer, 0, bufferDataLength - 1);

                    return Encoding.GetString(buffer, 0, bufferDataLength);
                }

                bufferDataLength++;

                // store last char for new line comparison
                lastChar = newChar;

                if (bufferDataLength == buffer.Length) Array.Resize(ref buffer, bufferDataLength * 2);
            }
        }
        finally
        {
            bufferPool.ReturnBuffer(bufferPoolBuffer);
        }

        if (bufferDataLength == 0) return null;

        return Encoding.GetString(buffer, 0, bufferDataLength);
    }

    /// <summary>
    ///     Base Stream.BeginRead will call this.Read and block thread (we don't want this, Network stream handles async)
    ///     In order to really async Reading Launch this.ReadAsync as Task will fire NetworkStream.ReadAsync
    ///     See Threads here :
    ///     https://github.com/justcoding121/Stream-Extended/pull/43
    ///     https://github.com/justcoding121/Titanium-Web-Proxy/issues/575
    /// </summary>
    /// <returns></returns>
    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
    {
        if (!networkStreamHack) return base.BeginRead(buffer, offset, count, callback, state);

        var vAsyncResult = ReadAsync(buffer, offset, count, cancellationToken);
        if (IsNetworkStream) vAsyncResult = vAsyncResult.WithCancellation(cancellationToken);

        vAsyncResult.ContinueWith(pAsyncResult =>
        {
            // use TaskExtended to pass State as AsyncObject
            // callback will call EndRead (otherwise, it will block)
            callback?.Invoke(new TaskResult<int>(pAsyncResult, state));
        }, cancellationToken);

        return vAsyncResult;
    }

    /// <summary>
    ///     override EndRead to handle async Reading (see BeginRead comment)
    /// </summary>
    /// <returns></returns>
    public override int EndRead(IAsyncResult asyncResult)
    {
        if (!networkStreamHack) return base.EndRead(asyncResult);

        return ((TaskResult<int>)asyncResult).Result;
    }

    /// <summary>
    ///     Fix the .net bug with SslStream slow WriteAsync
    ///     https://github.com/justcoding121/Titanium-Web-Proxy/issues/495
    ///     Stream.BeginWrite + Stream.BeginRead uses the same SemaphoreSlim(1)
    ///     That's why we need to call NetworkStream.BeginWrite only (while read is waiting SemaphoreSlim)
    /// </summary>
    /// <returns></returns>
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
    {
        if (!networkStreamHack) return base.BeginWrite(buffer, offset, count, callback, state);

        var vAsyncResult = WriteAsync(buffer, offset, count, cancellationToken);

        vAsyncResult.ContinueWith(pAsyncResult => { callback?.Invoke(new TaskResult(pAsyncResult, state)); },
            cancellationToken);

        return vAsyncResult;
    }

    public override void EndWrite(IAsyncResult asyncResult)
    {
        if (!networkStreamHack)
        {
            base.EndWrite(asyncResult);
            return;
        }

        ((TaskResult)asyncResult).GetResult();
    }

    /// <summary>
    ///     Writes a line async
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns></returns>
    public ValueTask WriteLineAsync(CancellationToken cancellationToken = default)
    {
        return WriteAsync(newLine, cancellationToken: cancellationToken);
    }

    private async ValueTask WriteAsyncInternal(string value, bool addNewLine, CancellationToken cancellationToken)
    {
        if (closedWrite) return;

        var newLineChars = addNewLine ? newLine.Length : 0;
        var charCount = value.Length;
        if (charCount < bufferPool.BufferSize - newLineChars)
        {
            var buffer = bufferPool.GetBuffer();
            try
            {
                var idx = Encoding.GetBytes(value, 0, charCount, buffer, 0);
                if (newLineChars > 0)
                {
                    Buffer.BlockCopy(newLine, 0, buffer, idx, newLineChars);
                    idx += newLineChars;
                }

                await BaseStream.WriteAsync(buffer, 0, idx, cancellationToken);
            }
            catch
            {
                closedWrite = true;
                if (!IsNetworkStream)
                    throw;
            }
            finally
            {
                bufferPool.ReturnBuffer(buffer);
            }
        }
        else
        {
            var buffer = new byte[charCount + newLineChars + 1];
            var idx = Encoding.GetBytes(value, 0, charCount, buffer, 0);
            if (newLineChars > 0)
            {
                Buffer.BlockCopy(newLine, 0, buffer, idx, newLineChars);
                idx += newLineChars;
            }

            try
            {
                await BaseStream.WriteAsync(buffer, 0, idx, cancellationToken);
            }
            catch
            {
                closedWrite = true;
                if (!IsNetworkStream)
                    throw;
            }
        }
    }

    public ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default)
    {
        return WriteAsyncInternal(value, true, cancellationToken);
    }

    /// <summary>
    ///     Write the headers to client
    /// </summary>
    /// <param name="headerBuilder"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal async Task WriteHeadersAsync(HeaderBuilder headerBuilder, CancellationToken cancellationToken = default)
    {
        var buffer = headerBuilder.GetBuffer();

        try
        {
            await WriteAsync(buffer.Array, buffer.Offset, buffer.Count, true, cancellationToken);
        }
        catch (IOException e)
        {
            //throw this as ServerConnectionException so that RetryPolicy can retry with a new server connection.
            if (this is HttpServerStream)
                throw new RetryableServerConnectionException(
                    "Server connection was closed. Exception while sending request line and headers.", e);

            throw;
        }
    }

    /// <summary>
    ///     Writes the data to the stream.
    /// </summary>
    /// <param name="data">The data.</param>
    /// <param name="flush">Should we flush after write?</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    internal async ValueTask WriteAsync(byte[] data, bool flush = false, CancellationToken cancellationToken = default)
    {
        if (closedWrite) return;

        try
        {
            await BaseStream.WriteAsync(data, 0, data.Length, cancellationToken);
            if (flush) await BaseStream.FlushAsync(cancellationToken);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    internal async Task WriteAsync(byte[] data, int offset, int count, bool flush,
        CancellationToken cancellationToken = default)
    {
        if (closedWrite) return;

        try
        {
            await BaseStream.WriteAsync(data, offset, count, cancellationToken);
            if (flush) await BaseStream.FlushAsync(cancellationToken);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    /// <summary>
    ///     Writes the byte array body to the stream; optionally chunked
    /// </summary>
    /// <param name="data"></param>
    /// <param name="isChunked"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal ValueTask WriteBodyAsync(byte[] data, bool isChunked, CancellationToken cancellationToken)
    {
        if (isChunked) return WriteBodyChunkedAsync(data, cancellationToken);

        return WriteAsync(data, cancellationToken: cancellationToken);
    }

    public async Task CopyBodyAsync(RequestResponseBase requestResponse, bool useOriginalHeaderValues,
        IHttpStreamWriter writer, TransformationMode transformation, bool isRequest, SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        var isChunked = useOriginalHeaderValues ? requestResponse.OriginalIsChunked : requestResponse.IsChunked;
        var contentLength = useOriginalHeaderValues
            ? requestResponse.OriginalContentLength
            : requestResponse.ContentLength;

        if (transformation == TransformationMode.None)
        {
            await CopyBodyAsync(writer, isChunked, contentLength, isRequest, args, cancellationToken);
            return;
        }

        LimitedStream limitedStream;
        Stream? decompressStream = null;

        var contentEncoding = useOriginalHeaderValues
            ? requestResponse.OriginalContentEncoding
            : requestResponse.ContentEncoding;

        Stream s = limitedStream = new LimitedStream(this, bufferPool, isChunked, contentLength);

        if (transformation == TransformationMode.Uncompress && contentEncoding != null)
            s = decompressStream =
                DecompressionFactory.Create(CompressionUtil.CompressionNameToEnum(contentEncoding), s);

        try
        {
            var http = new HttpStream(server, s, bufferPool, cancellationToken, true);
            await http.CopyBodyAsync(writer, false, -1, isRequest, args, cancellationToken);
        }
        finally
        {
            decompressStream?.Dispose();

            await limitedStream.Finish();
            limitedStream.Dispose();
        }
    }

    /// <summary>
    ///     Copies the specified content length number of bytes to the output stream from the given inputs stream
    ///     optionally chunked
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="isChunked"></param>
    /// <param name="contentLength"></param>
    /// <param name="onCopy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task CopyBodyAsync(IHttpStreamWriter writer, bool isChunked, long contentLength,
        bool isRequest,
        SessionEventArgs args, CancellationToken cancellationToken)
    {
#if DEBUG
            var isResponse = !isRequest;

            if (IsNetworkStream && writer.IsNetworkStream &&
                (isRequest && args.HttpClient.Request.OriginalHasBody && !args.HttpClient.Request.IsBodyRead && server.ShouldCallBeforeRequestBodyWrite()) ||
                (isResponse && args.HttpClient.Response.OriginalHasBody && !args.HttpClient.Response.IsBodyRead && server.ShouldCallBeforeResponseBodyWrite()))
            {
                return HandleBodyWrite(writer, isChunked, contentLength, isRequest, args, cancellationToken);
            }
#endif
        // For chunked request we need to read data as they arrive, until we reach a chunk end symbol
        if (isChunked) return CopyBodyChunkedAsync(writer, isRequest, args, cancellationToken);

        // http 1.0 or the stream reader limits the stream
        if (contentLength == -1) contentLength = long.MaxValue;

        // If not chunked then its easy just read the amount of bytes mentioned in content length header
        return CopyBytesToStream(writer, contentLength, isRequest, args, cancellationToken);
    }

    private Task HandleBodyWrite(IHttpStreamWriter writer, bool isChunked, long contentLength,
        bool isRequest, SessionEventArgs args, CancellationToken cancellationToken)
    {
        var originalContentLength = isRequest
            ? args.HttpClient.Request.OriginalContentLength
            : args.HttpClient.Response.OriginalContentLength;
        var originalIsChunked =
            isRequest ? args.HttpClient.Request.OriginalIsChunked : args.HttpClient.Response.OriginalIsChunked;

        //TODO
        //create a new decompression stream to wrap this source HttpStream based on original content encoding if needed.
        //create a new compression stream to wrap target writer stream based on content encoding if needed.

        //1. Begin while(true) loop
        //2. Parse chunk if chunked, and read bytes from original stream. Max length of bytes read will be equal to bufferPool.BufferSize.
        //3. Call BeforeBodyWrite event handler with BeforeBodyWriteEventArgs.BodyBytes set to the bytes read from original stream (pass null if original stream reached its end).
        //4. Write BeforeBodyWriteEventArgs.BodyBytes to the target stream when BeforeBodyWriteEventArgs.BodyBytes is not null or empty.
        //5. Stop writing to target stream when 'long contentLength' parameter number of bytes are written (when not chunked) or
        //when BeforeBodyWriteEventArgs.IsLastChunk is true after callback (when chunked).
        //6. Exit loop when original stream reaches its end AND when writing in step 5 has stopped.
        throw new NotImplementedException();
    }

    /// <summary>
    ///     Copies the given input bytes to output stream chunked
    /// </summary>
    /// <param name="data"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async ValueTask WriteBodyChunkedAsync(byte[] data, CancellationToken cancellationToken)
    {
        var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

        await WriteAsync(chunkHead, cancellationToken: cancellationToken);
        await WriteLineAsync(cancellationToken);
        await WriteAsync(data, cancellationToken: cancellationToken);
        await WriteLineAsync(cancellationToken);

        await WriteLineAsync("0", cancellationToken);
        await WriteLineAsync(cancellationToken);
    }

    /// <summary>
    ///     Copies the streams chunked
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="onCopy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task CopyBodyChunkedAsync(IHttpStreamWriter writer, bool isRequest, SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var chunkHead = await ReadLineAsync(cancellationToken);
            if (chunkHead == null) return;

            var idx = chunkHead.IndexOf(";", StringComparison.Ordinal);
            if (idx >= 0) chunkHead = chunkHead.Substring(0, idx);

            if (!int.TryParse(chunkHead, NumberStyles.HexNumber, null, out var chunkSize))
                throw new ProxyHttpException($"Invalid chunk length: '{chunkHead}'", null, null);

            await writer.WriteLineAsync(chunkHead, cancellationToken);

            if (chunkSize != 0) await CopyBytesToStream(writer, chunkSize, isRequest, args, cancellationToken);

            await writer.WriteLineAsync(cancellationToken);

            // chunk trail
            await ReadLineAsync(cancellationToken);

            if (chunkSize == 0) break;
        }
    }

    /// <summary>
    ///     Copies the specified bytes to the stream from the input stream
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="count"></param>
    /// <param name="onCopy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task CopyBytesToStream(IHttpStreamWriter writer, long count, bool isRequest, SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        var buffer = bufferPool.GetBuffer();

        try
        {
            var remainingBytes = count;

            while (remainingBytes > 0)
            {
                var bytesToRead = buffer.Length;
                if (remainingBytes < bytesToRead) bytesToRead = (int)remainingBytes;

                var bytesRead = await ReadAsync(buffer, 0, bytesToRead, cancellationToken);
                if (bytesRead == 0) break;

                remainingBytes -= bytesRead;

                await writer.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                if (isRequest)
                    args.OnDataSent(buffer, 0, bytesRead);
                else
                    args.OnDataReceived(buffer, 0, bytesRead);
            }
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    /// <summary>
    ///     Writes the request/response headers and body.
    /// </summary>
    /// <param name="requestResponse"></param>
    /// <param name="headerBuilder"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected async ValueTask WriteAsync(RequestResponseBase requestResponse, HeaderBuilder headerBuilder,
        CancellationToken cancellationToken = default)
    {
        var body = requestResponse.CompressBodyAndUpdateContentLength();
        headerBuilder.WriteHeaders(requestResponse.Headers);
        await WriteHeadersAsync(headerBuilder, cancellationToken);

        if (body != null)
        {
            await WriteBodyAsync(body, requestResponse.IsChunked, cancellationToken);
            requestResponse.IsBodySent = true;
        }
    }

#if NETSTANDARD2_1
        /// <summary>
        ///     Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream by the number of bytes written, and monitors cancellation requests.
        /// </summary>
        /// <param name="buffer">The buffer to write data from.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="P:System.Threading.CancellationToken.None" />.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken =
 default)
        {
            if (closedWrite)
            {
                return;
            }

            try
            {
                await BaseStream.WriteAsync(buffer, cancellationToken);
            }
            catch
            {
                closedWrite = true;
                if (!IsNetworkStream)
                    throw;
            }
        }
#else
    /// <summary>
    ///     Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream
    ///     by the number of bytes written, and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write data from.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>A task that represents the asynchronous write operation.</returns>
    public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        var buf = ArrayPool<byte>.Shared.Rent(buffer.Length);
        buffer.CopyTo(buf);
        try
        {
            await BaseStream.WriteAsync(buf, 0, buf.Length, cancellationToken);
        }
        catch
        {
            if (!IsNetworkStream)
                throw;
        }
    }
#endif
}