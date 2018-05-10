#if DEBUG
using System;
using System.IO;
using System.Text;
using System.Threading;
using StreamExtended;
using StreamExtended.Network;

namespace Titanium.Web.Proxy.Network
{
    internal class DebugCustomBufferedStream : CustomBufferedStream
    {
        private const string basePath = @".";

        private static int counter;

        private readonly FileStream fileStreamReceived;

        private readonly FileStream fileStreamSent;

        public DebugCustomBufferedStream(Guid connectionId, string type, Stream baseStream, IBufferPool bufferPool, int bufferSize, bool leaveOpen = false) 
            : base(baseStream, bufferPool, bufferSize, leaveOpen)
        {
            Counter = Interlocked.Increment(ref counter);
            fileStreamSent = new FileStream(Path.Combine(basePath, $"{connectionId}_{type}_{Counter}_sent.dat"), FileMode.Create);
            fileStreamReceived = new FileStream(Path.Combine(basePath, $"{connectionId}_{type}_{Counter}_received.dat"), FileMode.Create);
        }

        public int Counter { get; }

        protected override void OnDataWrite(byte[] buffer, int offset, int count)
        {
            fileStreamSent.Write(buffer, offset, count);
            Flush();
        }

        protected override void OnDataRead(byte[] buffer, int offset, int count)
        {
            fileStreamReceived.Write(buffer, offset, count);
            Flush();
        }

        public void LogException(Exception ex)
        {
            var data = Encoding.UTF8.GetBytes("EXCEPTION: " + ex);
            fileStreamReceived.Write(data, 0, data.Length);
            fileStreamReceived.Flush();
        }
        public override void Flush()
        {
            fileStreamSent.Flush(true);
            fileStreamReceived.Flush(true);

            if (CanWrite)
            {
                base.Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Flush();
                fileStreamSent.Dispose();
                fileStreamReceived.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
#endif
