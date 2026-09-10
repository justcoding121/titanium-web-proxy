using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Streams;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;

namespace Titanium.Web.Proxy.Http2
{
    internal partial class Http2Helper
    {
        /// <summary>
        ///     Per-direction HPACK + request-dispatch slot for <see cref="CopyHttp2FrameAsync"/>.
        ///     Allocated once per relay task (not per frame). Async methods cannot take <c>ref</c>,
        ///     so decoder/table-size/dispatch-chain live here instead of as locals on the frame loop.
        /// </summary>
        private sealed class CopyDirectionHpack
        {
            public Decoder? Decoder;
            public int HeaderTableSize;
            public Task RequestDispatchChain = Task.CompletedTask;
        }

        /// <summary>Serialize a write onto a connection-direction lock. Same shape as the Copy local helpers.</summary>
        private static async ValueTask LockedWriteAsync(SemaphoreSlim gate, CancellationToken cancellationToken,
            Func<ValueTask> writeAction)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await writeAction();
            }
            finally
            {
                gate.Release();
            }
        }

        private static int ReadHttp2FrameLength(byte[] frameHeaderBuffer) =>
            (frameHeaderBuffer[0] << 16) + (frameHeaderBuffer[1] << 8) + frameHeaderBuffer[2];

        private static int ReadHttp2StreamId(byte[] frameHeaderBuffer) =>
            ((frameHeaderBuffer[5] & 0x7f) << 24) + (frameHeaderBuffer[6] << 16) +
            (frameHeaderBuffer[7] << 8) + frameHeaderBuffer[8];

        private static int ReadHttp2UInt31(byte[] buffer) =>
            ((buffer[0] & 0x7f) << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3];

        private static int ReadHttp2ErrorCode(byte[] buffer) =>
            (buffer[0] << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3];

        private static void GetHttp2PaddedDataRange(byte[] buffer, int length, bool padded,
            out int dataOff, out int dataLen)
        {
            dataOff = padded ? 1 : 0;
            dataLen = padded ? length - 1 - buffer[0] : length;
            if (dataLen < 0)
                dataLen = 0;
        }
    }
}
