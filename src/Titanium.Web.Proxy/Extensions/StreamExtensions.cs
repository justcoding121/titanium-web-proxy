using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Extensions;

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
    internal static Task CopyToAsync(this Stream input, Stream output, Action<byte[], int, int> onCopy,
        IBufferPool bufferPool)
    {
        return CopyToAsync(input, output, onCopy, bufferPool, CancellationToken.None);
    }

    /// <summary>
    ///     Copy streams asynchronously
    /// </summary>
    /// <param name="input"></param>
    /// <param name="output"></param>
    /// <param name="onCopy"></param>
    /// <param name="bufferPool"></param>
    /// <param name="cancellationToken"></param>
    internal static async Task CopyToAsync(this Stream input, Stream output, Action<byte[], int, int>? onCopy,
        IBufferPool bufferPool, CancellationToken cancellationToken)
    {
        var buffer = bufferPool.GetBuffer();
        try
        {
            while (true)
            {
                int bytesRead;
                try
                {
                    // Read directly with the real cancellation token instead of the old
                    // Task<T>.WithCancellation(...) workaround (for "cancellation is not working on
                    // Socket ReadAsync", https://github.com/dotnet/corefx/issues/15033 - fixed upstream
                    // years ago, and HttpStream.ReadAsync/FillBufferAsync already carries its own
                    // narrower, still-needed workaround for that historical NetworkStream limitation).
                    // WithCancellation races the real read against a cancellation-triggered
                    // TaskCompletionSource and, the instant the token fires, returns 0 without ever
                    // awaiting the real read - abandoning it mid-flight rather than cancelling it. That
                    // read keeps running against this same `buffer` array in the background, so the
                    // `finally` below could return the buffer to the shared pool - and another relay
                    // could immediately reuse it - while the abandoned read was still writing into it,
                    // corrupting whichever connection borrowed it next. Awaiting the read directly lets
                    // it observe cancellation itself and actually stop before this method reuses its
                    // buffer.
                    bytesRead = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (bytesRead == 0) break;

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), CancellationToken.None);
                onCopy?.Invoke(buffer, 0, bytesRead);
            }
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    internal static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
        where T : struct
    {
        var tcs = new TaskCompletionSource<bool>();
        using (cancellationToken.Register(() => tcs.TrySetResult(true)))
        {
            if (task != await Task.WhenAny(task, tcs.Task)) return default;
        }

        return await task;
    }
}