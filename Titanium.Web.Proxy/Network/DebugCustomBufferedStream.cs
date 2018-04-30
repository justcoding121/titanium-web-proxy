#if DEBUG
using System.IO;
using System.Threading;
using StreamExtended.Network;

namespace Titanium.Web.Proxy.Network
{
    internal class DebugCustomBufferedStream : CustomBufferedStream
    {
        private const string basePath = @".";

        private static int counter;

        private readonly FileStream fileStreamReceived;

        private readonly FileStream fileStreamSent;

        public DebugCustomBufferedStream(Stream baseStream, int bufferSize) : base(baseStream, bufferSize)
        {
            Counter = Interlocked.Increment(ref counter);
            fileStreamSent = new FileStream(Path.Combine(basePath, $"{Counter}_sent.dat"), FileMode.Create);
            fileStreamReceived = new FileStream(Path.Combine(basePath, $"{Counter}_received.dat"), FileMode.Create);
        }

        public int Counter { get; }

        protected override void OnDataWrite(byte[] buffer, int offset, int count)
        {
            fileStreamSent.Write(buffer, offset, count);
        }

        protected override void OnDataRead(byte[] buffer, int offset, int count)
        {
            fileStreamReceived.Write(buffer, offset, count);
        }

        public override void Flush()
        {
            fileStreamSent.Flush(true);
            fileStreamReceived.Flush(true);
            base.Flush();
        }
    }
}
#endif
