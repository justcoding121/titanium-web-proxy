using System.Threading;
using System.Threading.Tasks;

namespace StreamExtended.Network
{
    /// <summary>
    ///     A concrete implementation of this interface is required when calling CopyStream.
    /// </summary>
    public interface ICustomStreamWriter
    {
        void Write(byte[] buffer, int i, int bufferLength);

        Task WriteAsync(byte[] buffer, int i, int bufferLength, CancellationToken cancellationToken);
    }
}