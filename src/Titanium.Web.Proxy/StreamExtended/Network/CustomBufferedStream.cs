using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.StreamExtended.Network
{
    /// <summary>
    ///     A custom network stream inherited from stream
    ///     with an underlying read buffer supporting both read/write 
    ///     of UTF-8 encoded string or raw bytes asynchronously from last read position.
    /// </summary>
    /// <seealso cref="System.IO.Stream" />
    public class CustomBufferedStream : Stream, ICustomStreamReader
    {
        private readonly bool leaveOpen;
        private byte[] streamBuffer;

        // default to UTF-8
        private static readonly Encoding encoding = Encoding.UTF8;

        private static bool networkStreamHack = true;

        private int bufferLength;

        private int bufferPos;

        private bool disposed;

        private bool closed;

        private readonly IBufferPool bufferPool;

        public int BufferSize { get; }

        public event EventHandler<DataEventArgs> DataRead;

        public event EventHandler<DataEventArgs> DataWrite;

        public Stream BaseStream { get; }

        public bool IsClosed => closed;

        static CustomBufferedStream()
        {
            // TODO: remove this hack when removing .NET 4.x support
            try
            {
                var method = typeof(NetworkStream).GetMethod(nameof(Stream.ReadAsync),
                    new Type[] { typeof(byte[]), typeof(int), typeof(int), typeof(CancellationToken) });
                if (method != null && method.DeclaringType != typeof(Stream))
                {
                    networkStreamHack = false;
                }
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CustomBufferedStream"/> class.
        /// </summary>
        /// <param name="baseStream">The base stream.</param>
        /// <param name="bufferPool">Bufferpool.</param>
        /// <param name="bufferSize">Size of the buffer.</param>
        /// <param name="leaveOpen"><see langword="true" /> to leave the stream open after disposing the <see cref="T:CustomBufferedStream" /> object; otherwise, <see langword="false" />.</param>
        public CustomBufferedStream(Stream baseStream, IBufferPool bufferPool, int bufferSize, bool leaveOpen = false)
        {
            BaseStream = baseStream;
            BufferSize = bufferSize;
            this.leaveOpen = leaveOpen;
            streamBuffer = bufferPool.GetBuffer(bufferSize);
            this.bufferPool = bufferPool;
        }

        /// <summary>
        /// When overridden in a derived class, clears all buffers for this stream and causes any buffered data to be written to the underlying device.
        /// </summary>
        public override void Flush()
        {
            BaseStream.Flush();
        }

        /// <summary>
        /// When overridden in a derived class, sets the position within the current stream.
        /// </summary>
        /// <param name="offset">A byte offset relative to the <paramref name="origin" /> parameter.</param>
        /// <param name="origin">A value of type <see cref="T:System.IO.SeekOrigin" /> indicating the reference point used to obtain the new position.</param>
        /// <returns>
        /// The new position within the current stream.
        /// </returns>
        public override long Seek(long offset, SeekOrigin origin)
        {
            bufferLength = 0;
            bufferPos = 0;
            return BaseStream.Seek(offset, origin);
        }

        /// <summary>
        /// When overridden in a derived class, sets the length of the current stream.
        /// </summary>
        /// <param name="value">The desired length of the current stream in bytes.</param>
        public override void SetLength(long value)
        {
            BaseStream.SetLength(value);
        }

        /// <summary>
        /// When overridden in a derived class, reads a sequence of bytes from the current stream and advances the position within the stream by the number of bytes read.
        /// </summary>
        /// <param name="buffer">An array of bytes. When this method returns, the buffer contains the specified byte array with the values between <paramref name="offset" /> and (<paramref name="offset" /> + <paramref name="count" /> - 1) replaced by the bytes read from the current source.</param>
        /// <param name="offset">The zero-based byte offset in <paramref name="buffer" /> at which to begin storing the data read from the current stream.</param>
        /// <param name="count">The maximum number of bytes to be read from the current stream.</param>
        /// <returns>
        /// The total number of bytes read into the buffer. This can be less than the number of bytes requested if that many bytes are not currently available, or zero (0) if the end of the stream has been reached.
        /// </returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (bufferLength == 0)
            {
                FillBuffer();
            }

            int available = Math.Min(bufferLength, count);
            if (available > 0)
            {
                Buffer.BlockCopy(streamBuffer, bufferPos, buffer, offset, available);
                bufferPos += available;
                bufferLength -= available;
            }

            return available;
        }

        /// <summary>
        /// When overridden in a derived class, writes a sequence of bytes to the current stream and advances the current position within this stream by the number of bytes written.
        /// </summary>
        /// <param name="buffer">An array of bytes. This method copies <paramref name="count" /> bytes from <paramref name="buffer" /> to the current stream.</param>
        /// <param name="offset">The zero-based byte offset in <paramref name="buffer" /> at which to begin copying bytes to the current stream.</param>
        /// <param name="count">The number of bytes to be written to the current stream.</param>
        [DebuggerStepThrough]
        public override void Write(byte[] buffer, int offset, int count)
        {
            OnDataWrite(buffer, offset, count);
            BaseStream.Write(buffer, offset, count);
        }

        /// <summary>
        /// Asynchronously reads the bytes from the current stream and writes them to another stream, using a specified buffer size and cancellation token.
        /// </summary>
        /// <param name="destination">The stream to which the contents of the current stream will be copied.</param>
        /// <param name="bufferSize">The size, in bytes, of the buffer. This value must be greater than zero. The default size is 81920.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="P:System.Threading.CancellationToken.None" />.</param>
        /// <returns>
        /// A task that represents the asynchronous copy operation.
        /// </returns>
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken = default)
        {
            if (bufferLength > 0)
            {
                await destination.WriteAsync(streamBuffer, bufferPos, bufferLength, cancellationToken);

                bufferLength = 0;
            }

            await base.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        /// <summary>
        /// Asynchronously clears all buffers for this stream, causes any buffered data to be written to the underlying device, and monitors cancellation requests.
        /// </summary>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="P:System.Threading.CancellationToken.None" />.</param>
        /// <returns>
        /// A task that represents the asynchronous flush operation.
        /// </returns>
        public override Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return BaseStream.FlushAsync(cancellationToken);
        }

        /// <summary>
        /// Asynchronously reads a sequence of bytes from the current stream,
        /// advances the position within the stream by the number of bytes read,
        /// and monitors cancellation requests.
        /// </summary>
        /// <param name="buffer">The buffer to write the data into.</param>
        /// <param name="offset">The byte offset in <paramref name="buffer" /> at which 
        /// to begin writing data from the stream.</param>
        /// <param name="count">The maximum number of bytes to read.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. 
        /// The default value is <see cref="P:System.Threading.CancellationToken.None" />.</param>
        /// <returns>
        /// A task that represents the asynchronous read operation.
        /// The value of the parameter contains the total 
        /// number of bytes read into the buffer.
        /// The result value can be less than the number of bytes
        /// requested if the number of bytes currently available is
        /// less than the requested number, or it can be 0 (zero)
        /// if the end of the stream has been reached.
        /// </returns>
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
            if (bufferLength == 0)
            {
                await FillBufferAsync(cancellationToken);
            }

            int available = Math.Min(bufferLength, count);
            if (available > 0)
            {
                Buffer.BlockCopy(streamBuffer, bufferPos, buffer, offset, available);
                bufferPos += available;
                bufferLength -= available;
            }

            return available;
        }

        /// <summary>
        /// Reads a byte from the stream and advances the position within the stream by one byte, or returns -1 if at the end of the stream.
        /// </summary>
        /// <returns>
        /// The unsigned byte cast to an Int32, or -1 if at the end of the stream.
        /// </returns>
        public override int ReadByte()
        {
            if (bufferLength == 0)
            {
                FillBuffer();
            }

            if (bufferLength == 0)
            {
                return -1;
            }

            bufferLength--;
            return streamBuffer[bufferPos++];
        }

        /// <summary>
        /// Peeks a byte asynchronous.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public async Task<int> PeekByteAsync(int index, CancellationToken cancellationToken = default)
        {
            if (Available <= index)
            {
                await FillBufferAsync(cancellationToken);
            }

            //When index is greater than the buffer size
            if (streamBuffer.Length <= index)
            {
                throw new Exception("Requested Peek index exceeds the buffer size. Consider increasing the buffer size.");
            }

            //When index is greater than the buffer size
            if (Available <= index)
            {
                return -1;
            }
            
            return streamBuffer[bufferPos + index];
        }

        /// <summary>
        /// Peeks bytes asynchronous.
        /// </summary>
        /// <param name="buffer">The buffer to copy.</param>
        /// <param name="offset">The offset where copying.</param>
        /// <param name="index">The index.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public async Task<int> PeekBytesAsync(byte[] buffer, int offset, int index, int size, CancellationToken cancellationToken = default)
        {
            if (Available <= index)
            {
                await FillBufferAsync(cancellationToken);
            }

            //When index is greater than the buffer size
            if (streamBuffer.Length <= (index + size))
            {
                throw new Exception("Requested Peek index and size exceeds the buffer size. Consider increasing the buffer size.");
            }

            if (Available <= (index + size))
            {
                return -1;
            }

            Buffer.BlockCopy(streamBuffer, index, buffer, offset, size);

            return size;
        }

        /// <summary>
        /// Peeks a byte from buffer.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <returns></returns>
        /// <exception cref="Exception">Index is out of buffer size</exception>
        public byte PeekByteFromBuffer(int index)
        {
            if (bufferLength <= index)
            {
                throw new Exception("Index is out of buffer size");
            }

            return streamBuffer[bufferPos + index];
        }

        /// <summary>
        /// Reads a byte from buffer.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception">Buffer is empty</exception>
        public byte ReadByteFromBuffer()
        {
            if (bufferLength == 0)
            {
                throw new Exception("Buffer is empty");
            }

            bufferLength--;
            return streamBuffer[bufferPos++];
        }

        /// <summary>
        /// Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream by the number of bytes written, and monitors cancellation requests.
        /// </summary>
        /// <param name="buffer">The buffer to write data from.</param>
        /// <param name="offset">The zero-based byte offset in <paramref name="buffer" /> from which to begin copying bytes to the stream.</param>
        /// <param name="count">The maximum number of bytes to write.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="P:System.Threading.CancellationToken.None" />.</param>
        /// <returns>
        /// A task that represents the asynchronous write operation.
        /// </returns>
        [DebuggerStepThrough]
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
            OnDataWrite(buffer, offset, count);

            await BaseStream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        /// <summary>
        /// Writes a byte to the current position in the stream and advances the position within the stream by one byte.
        /// </summary>
        /// <param name="value">The byte to write to the stream.</param>
        public override void WriteByte(byte value)
        {
            var buffer = bufferPool.GetBuffer(BufferSize);
            try
            {
                buffer[0] = value;
                OnDataWrite(buffer, 0, 1);
                BaseStream.Write(buffer, 0, 1);
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
        /// Releases the unmanaged resources used by the <see cref="T:System.IO.Stream" /> and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                closed = true;
                if (!leaveOpen)
                {
                    BaseStream.Dispose();
                }

                var buffer = streamBuffer;
                streamBuffer = null;
                bufferPool.ReturnBuffer(buffer);
            }
        }

        /// <summary>
        /// When overridden in a derived class, gets a value indicating whether the current stream supports reading.
        /// </summary>
        public override bool CanRead => BaseStream.CanRead;

        /// <summary>
        /// When overridden in a derived class, gets a value indicating whether the current stream supports seeking.
        /// </summary>
        public override bool CanSeek => BaseStream.CanSeek;

        /// <summary>
        /// When overridden in a derived class, gets a value indicating whether the current stream supports writing.
        /// </summary>
        public override bool CanWrite => BaseStream.CanWrite;

        /// <summary>
        /// Gets a value that determines whether the current stream can time out.
        /// </summary>
        public override bool CanTimeout => BaseStream.CanTimeout;

        /// <summary>
        /// When overridden in a derived class, gets the length in bytes of the stream.
        /// </summary>
        public override long Length => BaseStream.Length;

        /// <summary>
        /// Gets a value indicating whether data is available.
        /// </summary>
        public bool DataAvailable => bufferLength > 0;

        /// <summary>
        /// Gets the available data size.
        /// </summary>
        public int Available => bufferLength;

        /// <summary>
        /// When overridden in a derived class, gets or sets the position within the current stream.
        /// </summary>
        public override long Position
        {
            get => BaseStream.Position;
            set => BaseStream.Position = value;
        }

        /// <summary>
        /// Gets or sets a value, in miliseconds, that determines how long the stream will attempt to read before timing out.
        /// </summary>
        public override int ReadTimeout
        {
            get => BaseStream.ReadTimeout;
            set => BaseStream.ReadTimeout = value;
        }

        /// <summary>
        /// Gets or sets a value, in miliseconds, that determines how long the stream will attempt to write before timing out.
        /// </summary>
        public override int WriteTimeout
        {
            get => BaseStream.WriteTimeout;
            set => BaseStream.WriteTimeout = value;
        }

        /// <summary>
        /// Fills the buffer.
        /// </summary>
        public bool FillBuffer()
        {
            if (closed)
            {
                throw new Exception("Stream is already closed");
            }

            if (bufferLength > 0)
            {
                //normally we fill the buffer only when it is empty, but sometimes we need more data
                //move the remaining data to the beginning of the buffer 
                Buffer.BlockCopy(streamBuffer, bufferPos, streamBuffer, 0, bufferLength);
            }

            bufferPos = 0;

            bool result = false;
            try
            {
                int readBytes = BaseStream.Read(streamBuffer, bufferLength, streamBuffer.Length - bufferLength);
                result = readBytes > 0;
                if (result)
                {
                    OnDataRead(streamBuffer, bufferLength, readBytes);
                    bufferLength += readBytes;
                }
            }
            finally
            {
                if (!result)
                {
                    closed = true;
                }
            }

            return result;

        }

        /// <summary>
        /// Fills the buffer asynchronous.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public async Task<bool> FillBufferAsync(CancellationToken cancellationToken = default)
        {
            if (closed)
            {
                throw new Exception("Stream is already closed");
            }

            if (bufferLength > 0)
            {
                //normally we fill the buffer only when it is empty, but sometimes we need more data
                //move the remaining data to the beginning of the buffer 
                Buffer.BlockCopy(streamBuffer, bufferPos, streamBuffer, 0, bufferLength);
            }

            int bytesToRead = streamBuffer.Length - bufferLength;
            if (bytesToRead == 0)
            {
                return false;
            }

            bufferPos = 0;

            bool result = false;
            try
            {
                int readBytes = await BaseStream.ReadAsync(streamBuffer, bufferLength, bytesToRead, cancellationToken);
                result = readBytes > 0;
                if (result)
                {
                    OnDataRead(streamBuffer, bufferLength, readBytes);
                    bufferLength += readBytes;
                }
            }
            finally
            {
                if (!result)
                {
                    closed = true;
                }
            }

            return result;
        }

        /// <summary>
        /// Read a line from the byte stream
        /// </summary>
        /// <returns></returns>
        public Task<string> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            return ReadLineInternalAsync(this, bufferPool, cancellationToken);
        }

        /// <summary>
        /// Read a line from the byte stream
        /// </summary>
        /// <returns></returns>
        internal static async Task<string> ReadLineInternalAsync(ICustomStreamReader reader, IBufferPool bufferPool, CancellationToken cancellationToken = default)
        {
            byte lastChar = default;

            int bufferDataLength = 0;

            // try to use buffer from the buffer pool, usually it is enough
            var bufferPoolBuffer = bufferPool.GetBuffer(reader.BufferSize);
            var buffer = bufferPoolBuffer;

            try
            {
                while (reader.DataAvailable || await reader.FillBufferAsync(cancellationToken))
                {
                    byte newChar = reader.ReadByteFromBuffer();
                    buffer[bufferDataLength] = newChar;

                    //if new line
                    if (newChar == '\n')
                    {
                        if (lastChar == '\r')
                        {
                            return encoding.GetString(buffer, 0, bufferDataLength - 1);
                        }

                        return encoding.GetString(buffer, 0, bufferDataLength);
                    }

                    bufferDataLength++;

                    //store last char for new line comparison
                    lastChar = newChar;

                    if (bufferDataLength == buffer.Length)
                    {
                        ResizeBuffer(ref buffer, bufferDataLength * 2);
                    }
                }
            }
            finally
            {
                bufferPool.ReturnBuffer(bufferPoolBuffer);
            }

            if (bufferDataLength == 0)
            {
                return null;
            }

            return encoding.GetString(buffer, 0, bufferDataLength);
        }

        /// <summary>
        /// Read until the last new line, ignores the result
        /// </summary>
        /// <returns></returns>
        public async Task ReadAndIgnoreAllLinesAsync(CancellationToken cancellationToken = default)
        {
            while (!string.IsNullOrEmpty(await ReadLineAsync(cancellationToken)))
            {
            }
        }

        /// <summary>
        /// Increase size of buffer and copy existing content to new buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="size"></param>
        private static void ResizeBuffer(ref byte[] buffer, long size)
        {
            var newBuffer = new byte[size];
            Buffer.BlockCopy(buffer, 0, newBuffer, 0, buffer.Length);
            buffer = newBuffer;
        }

        /// <summary>        
        /// Base Stream.BeginRead will call this.Read and block thread (we don't want this, Network stream handles async)
        /// In order to really async Reading Launch this.ReadAsync as Task will fire NetworkStream.ReadAsync
        /// See Threads here :
        /// https://github.com/justcoding121/Stream-Extended/pull/43
        /// https://github.com/justcoding121/Titanium-Web-Proxy/issues/575
        /// </summary>
        /// <returns></returns>
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            if (!networkStreamHack)
            {
                return base.BeginRead(buffer, offset, count, callback, state);
            }

            var vAsyncResult = this.ReadAsync(buffer, offset, count);

            vAsyncResult.ContinueWith(pAsyncResult =>
            {
                //use TaskExtended to pass State as AsyncObject
                //callback will call EndRead (otherwise, it will block)
                callback?.Invoke(new TaskResult<int>(pAsyncResult, state));
            });

            return vAsyncResult;
        }

        /// <summary>
        /// override EndRead to handle async Reading (see BeginRead comment)
        /// </summary>
        /// <returns></returns>
        public override int EndRead(IAsyncResult asyncResult)
        {
            if (!networkStreamHack)
            {
                return base.EndRead(asyncResult);
            }

            return ((TaskResult<int>)asyncResult).Result;
        }


        /// <summary>
        /// Fix the .net bug with SslStream slow WriteAsync
        /// https://github.com/justcoding121/Titanium-Web-Proxy/issues/495
        /// Stream.BeginWrite + Stream.BeginRead uses the same SemaphoreSlim(1)
        /// That's why we need to call NetworkStream.BeginWrite only (while read is waiting SemaphoreSlim)
        /// </summary>
        /// <returns></returns>
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            if (!networkStreamHack)
            {
                return base.BeginWrite(buffer, offset, count, callback, state);
            }

            var vAsyncResult = this.WriteAsync(buffer, offset, count);

            vAsyncResult.ContinueWith(pAsyncResult =>
            {
                callback?.Invoke(new TaskResult(pAsyncResult, state));
            });

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
    }
}
