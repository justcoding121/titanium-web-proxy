using System.Threading;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using HttpKnownHeaders = Titanium.Web.Proxy.Http.KnownHeaders;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    private int _overloadLevel;
    private volatile bool _overloadEnabled;
    private volatile bool _overloadStopAccepting;
    private volatile bool _overloadDisableKeepAlive;
    private volatile bool _overloadRejectNewRequests;
    private int _overloadRetryAfterSeconds = 1;

    /// <summary>True when <c>server.overload.enabled</c> is on.</summary>
    public bool OverloadProtectionEnabled => _overloadEnabled;

    /// <summary>Current <see cref="Resilience.OverloadLevel"/> as 0, 1, or 2.</summary>
    public int OverloadLevel => Volatile.Read(ref _overloadLevel);

    public void SetOverloadRuntime(
        bool enabled,
        int level,
        bool stopAccepting,
        bool disableKeepAlive,
        bool rejectNewRequests,
        int retryAfterSeconds)
    {
        _overloadEnabled = enabled;
        Volatile.Write(ref _overloadLevel, level);
        _overloadStopAccepting = stopAccepting;
        _overloadDisableKeepAlive = disableKeepAlive;
        _overloadRejectNewRequests = rejectNewRequests;
        _overloadRetryAfterSeconds = retryAfterSeconds <= 0 ? 1 : retryAfterSeconds;
    }

    /// <summary>503 with Retry-After when overload rejects new requests; otherwise null.</summary>
    internal int? TryGetOverloadRejectionStatus()
    {
        if (!_overloadEnabled || !_overloadRejectNewRequests)
        {
            return null;
        }

        return Volatile.Read(ref _overloadLevel) >= (int)Resilience.OverloadLevel.Critical ? 503 : null;
    }

    internal bool ShouldOverloadStopAccepting =>
        _overloadEnabled && _overloadStopAccepting &&
        Volatile.Read(ref _overloadLevel) >= (int)Resilience.OverloadLevel.Critical;

    internal bool ShouldDisableKeepAlive =>
        _overloadEnabled && _overloadDisableKeepAlive &&
        Volatile.Read(ref _overloadLevel) >= (int)Resilience.OverloadLevel.Elevated;

    internal void ApplyOverloadConnectionCloseIfNeeded(SessionEventArgsBase session)
    {
        if (!ShouldDisableKeepAlive || session.HttpClient.Request.HttpVersion.Major != 1)
        {
            return;
        }

        session.HttpClient.Response.Headers.AddHeader("Connection", "close");
    }

    internal void StageOverloadRejection(SessionEventArgs session)
    {
        var retry = _overloadRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        session.HttpClient.Response.StatusCode = 503;
        session.HttpClient.Response.Headers.AddHeader("Retry-After", retry);
    }
}
