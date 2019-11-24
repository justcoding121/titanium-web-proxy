using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.Network;

internal class NullWriter : IHttpStreamWriter
{
    public static NullWriter Instance { get; } = new NullWriter();

    public void Write(byte[] buffer, int offset, int count)
    {
    }

#if NET45
    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
    }

#else
    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
#endif

    public ValueTask WriteLineAsync(CancellationToken cancellationToken = default)
    {
        throw new System.NotImplementedException();
    }

    public ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default)
    {
        throw new System.NotImplementedException();
    }
}
