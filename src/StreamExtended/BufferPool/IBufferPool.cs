using System;

namespace StreamExtended
{
    /// <summary>
    ///     Use this interface to implement custom buffer pool.
    ///     To use the default buffer pool implementation use DefaultBufferPool class.
    /// </summary>
    public interface IBufferPool : IDisposable
    {
        byte[] GetBuffer(int bufferSize);
        void ReturnBuffer(byte[] buffer);
    }
}
