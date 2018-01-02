#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended.Network;

namespace Titanium.Web.Proxy.Network
{
    class DebugCustomBufferedStream : CustomBufferedStream
    {
        private const string basePath = @"c:\\11\\";

        private static int counter;

        public int Counter { get; }

        private FileStream fileStreamSent;

        private FileStream fileStreamReceived;

        public DebugCustomBufferedStream(Stream baseStream, int bufferSize) : base(baseStream, bufferSize)
        {
            Counter = Interlocked.Increment(ref counter);
            fileStreamSent = new FileStream(Path.Combine(basePath, $"{Counter}_sent.dat"), FileMode.Create);
            fileStreamReceived = new FileStream(Path.Combine(basePath, $"{Counter}_received.dat"), FileMode.Create);
        }

        protected override void OnDataSent(byte[] buffer, int offset, int count)
        {
            fileStreamSent.Write(buffer, offset, count);
        }

        protected override void OnDataReceived(byte[] buffer, int offset, int count)
        {
            fileStreamReceived.Write(buffer, offset, count);
        }

        public void Flush()
        {
            fileStreamSent.Flush(true);
            fileStreamReceived.Flush(true);
        }
    }
}
#endif