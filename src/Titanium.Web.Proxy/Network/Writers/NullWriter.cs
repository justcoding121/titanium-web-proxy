using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers;

internal class NullWriter : IHttpStreamWriter
{
    private NullWriter()
    {
    }

    public static NullWriter Instance { get; } = new();

    public bool IsNetworkStream => false;

    public void Write(byte[] buffer, int offset, int count)
    {
    }

    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
#if NET451
        return Net45Compatibility.CompletedTask;
#else
            return Task.CompletedTask;
#endif
    }

    public ValueTask WriteLineAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}