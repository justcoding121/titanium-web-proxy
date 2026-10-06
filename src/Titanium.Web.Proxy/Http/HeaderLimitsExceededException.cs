using System;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     An HTTP/1 header block exceeded <c>MaxHeaderCount</c> or <c>MaxHeaderAggregateBytes</c>
///     while <c>HeaderLimits</c> was <c>Enforce</c>. Not an <see cref="System.IO.IOException"/>,
///     so it is not treated as a client disconnect.
/// </summary>
public sealed class HeaderLimitsExceededException : Exception
{
    /// <summary>Creates an exception for one header-block breach.</summary>
    public HeaderLimitsExceededException(bool isCount, long observed, long limit, bool isRequest)
        : base(isCount
            ? $"Header count {observed} exceeded limit {limit}."
            : $"Header block size {observed} exceeded limit {limit}.")
    {
        IsCount = isCount;
        Observed = observed;
        Limit = limit;
        IsRequest = isRequest;
    }

    /// <summary>True when the breach is the header count. False when it is the byte total.</summary>
    public bool IsCount { get; }

    /// <summary>The count or byte total that crossed the limit.</summary>
    public long Observed { get; }

    /// <summary>The configured limit that was crossed.</summary>
    public long Limit { get; }

    /// <summary>True when the breach was on request headers.</summary>
    public bool IsRequest { get; }
}
