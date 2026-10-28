using System;

namespace Titanium.Web.Proxy.UnitTests.Support;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utc = DateTimeOffset.UnixEpoch;

    public void Advance(TimeSpan by) => _utc += by;

    public override DateTimeOffset GetUtcNow() => _utc;

    public override long GetTimestamp() => _utc.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}
