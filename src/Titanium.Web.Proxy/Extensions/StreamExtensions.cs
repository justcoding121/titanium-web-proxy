using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Extensions
{
    /// <summary>
    ///     Extensions used for Stream and CustomBinaryReader objects
    /// </summary>
    internal static class StreamExtensions
    {
        /// <summary>
        ///     Copy streams asynchronously
        /// </summary>
        /// <param name="input"></param>
        /// <param name="output"></param>
        /// <param name="onCopy"></param>
        /// <param name="bufferPool"></param>
        /// <param name="bufferSize"></param>
        internal static Task CopyToAsync(this Stream input, Stream output, Action<byte[], int, int> onCopy,
            IBufferPool bufferPool, int bufferSize)
        {
            return CopyToAsync(input, output, onCopy, bufferPool, bufferSize, CancellationToken.None);
        }

        /// <summary>
        ///     Copy streams asynchronously
        /// </summary>
        /// <param name="input"></param>
        /// <param name="output"></param>
        /// <param name="onCopy"></param>
        /// <param name="bufferPool"></param>
        /// <param name="bufferSize"></param>
        /// <param name="cancellationToken"></param>
        internal static async Task CopyToAsync(this Stream input, Stream output, Action<byte[], int, int> onCopy,
            IBufferPool bufferPool, int bufferSize, CancellationToken cancellationToken)
        {
            var buffer = bufferPool.GetBuffer(bufferSize);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // cancellation is not working on Socket ReadAsync
                    // https://github.com/dotnet/corefx/issues/15033
                    int num = await input.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)
                        .withCancellation(cancellationToken);
                    int bytesRead;
                    if ((bytesRead = num) != 0 && !cancellationToken.IsCancellationRequested)
                    {
                        await output.WriteAsync(buffer, 0, bytesRead, CancellationToken.None);
                        onCopy?.Invoke(buffer, 0, bytesRead);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            finally
            {
                bufferPool.ReturnBuffer(buffer);
            }
        }

        private static async Task<T> withCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task))
                {
                    return default;
                }
            }

            return await task;
        }
    }
}
