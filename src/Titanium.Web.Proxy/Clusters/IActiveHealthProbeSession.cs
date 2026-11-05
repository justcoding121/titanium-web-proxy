using System;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>
///     One active-health probe started by <c>IActiveHealthProbeProvider</c>.
///     Core uses this to run a single round from tests. Dispose stops the loop.
/// </summary>
internal interface IActiveHealthProbeSession : IDisposable
{
    Task ProbeOnceAsync(CancellationToken cancellationToken = default);
}
