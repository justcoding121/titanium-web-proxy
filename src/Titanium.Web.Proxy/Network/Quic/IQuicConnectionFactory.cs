using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Quic;

/// <summary>
///     Creates outbound QUIC connections to HTTP/3 origins. Extracted so
///     <see cref="QuicConnectionPool" /> policy (share, invalidate, warmup) can be unit-tested
///     without MsQuic.
/// </summary>
internal interface IQuicConnectionFactory
{
    Task<QuicServerConnection> CreateAsync( // NOSONAR S107 -- Factory contract keeps connection identity and transport policy explicit.
        string connectHost,
        string sniHost,
        int port,
        IPEndPoint? upStreamEndPoint,
        IExternalProxy? upStreamProxy,
        string cacheKey,
        RemoteCertificateValidationCallback? remoteCertificateValidationCallback,
        CancellationToken cancellationToken);
}
