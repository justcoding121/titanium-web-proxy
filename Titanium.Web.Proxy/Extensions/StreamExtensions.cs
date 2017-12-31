using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using StreamExtended.Network;

namespace Titanium.Web.Proxy.Extensions
{
    /// <summary>
    /// Extensions used for Stream and CustomBinaryReader objects
    /// </summary>
    internal static class StreamExtensions
    {
        /// <summary>
        /// Copy streams asynchronously
        /// </summary>
        /// <param name="input"></param>
        /// <param name="output"></param>
        /// <param name="onCopy"></param>
        /// <param name="bufferSize"></param>
        internal static async Task CopyToAsync(this Stream input, Stream output, Action<byte[], int, int> onCopy, int bufferSize)
        {
            byte[] buffer = new byte[bufferSize];
            while (true)
            {
                int num = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                int bytesRead;
                if ((bytesRead = num) != 0)
                {
                    await output.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                    onCopy?.Invoke(buffer, 0, bytesRead);
                }
                else
                {
                    break;
                }
            }
        }
    }
}
