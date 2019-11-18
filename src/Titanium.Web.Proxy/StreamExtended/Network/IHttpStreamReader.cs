﻿using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.StreamExtended.Network
{
    public interface IHttpStreamReader : ILineStream
    {
        int Read(byte[] buffer, int offset, int count);

        Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    }
}
