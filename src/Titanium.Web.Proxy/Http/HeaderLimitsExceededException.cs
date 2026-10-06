using System;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     An HTTP/1 header block exceeded <c>MaxHeaderCount</c> or <c>MaxHeaderAggregateBytes</c>
///     while <c>HeaderLimits</c> was <c>Enforce</c>. Not an <see cref="System.IO.IOException"/>,
///     so it is not treated as a client disconnect.
/// </summary>
internal sealed class HeaderLimitsExceededException : Exception
{
    internal HeaderLimitsExceededException(bool isCount, long observed, long limit, bool isRequest)
        : base(isCount
            ? $"Header count {observed} exceeded limit {limit}."
            : $"Header block size {observed} exceeded limit {limit}.")
    {
        IsCount = isCount;
        Observed = observed;
        Limit = limit;
        IsRequest = isRequest;
    }

    internal bool IsCount { get; }
    internal long Observed { get; }
    internal long Limit { get; }
    internal bool IsRequest { get; }
}
