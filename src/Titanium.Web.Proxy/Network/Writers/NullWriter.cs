using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     A discard writer used to drain (read and throw away) a body/trailer from the wire, e.g. when
///     syphoning out an unread request/response body so a connection can be safely reused. Every write is
///     a deliberate no-op rather than an error - a caller passing <see cref="Instance" /> is explicitly
///     asking for the data to be discarded, not signaling a programming mistake.
/// </summary>
internal class NullWriter : IHttpStreamWriter, ITransportCapableStream
{
    private NullWriter()
    {
    }

    public static NullWriter Instance { get; } = new();

    public bool IsNetworkStream => false;

    public bool SupportsBodyWriteHook => false;

    public void Write(byte[] buffer, int offset, int count)
    {
    }

    public ValueTask WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return default;
    }

    public ValueTask WriteLineAsync(CancellationToken cancellationToken = default)
    {
        return default;
    }

    public ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default)
    {
        return default;
    }
}
