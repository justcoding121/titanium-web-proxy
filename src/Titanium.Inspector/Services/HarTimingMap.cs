using Titanium.Web.Proxy.Diagnostics;

namespace Titanium.Inspector.Services;

/// <summary>
/// Maps engine timestamps onto HAR 1.2 timings. <c>ssl</c> is included in <c>connect</c> and is
/// not added again when the phases are summed. A reused or prefetched connection contributes no
/// DNS, connect, or TLS time. Retries leave only the last attempt's marks, so the gap stays in
/// <c>blocked</c> and <c>receive</c> is the remainder that makes the sum equal <c>time</c>.
/// </summary>
public static class HarTimingMap
{
    public readonly record struct Phases(
        double BlockedMs,
        double DnsMs,
        double ConnectMs,
        double SslMs,
        double SendMs,
        double WaitMs,
        double ReceiveMs,
        bool Reused);

    public static Phases Map(HttpRequestTiming timing, UpstreamConnectionTiming? connection)
    {
        ArgumentNullException.ThrowIfNull(timing);
        var total = Tenth(timing.TotalDuration.TotalMilliseconds);
        var reused = timing.UpstreamConnectionReused;
        var attribute = !reused
            && connection is not null
            && connection.ConnectStartedAt >= timing.SessionCreatedAt;

        var dns = attribute ? TenthSpan(connection!.DnsDuration) : -1;
        var ssl = attribute ? TenthSpan(connection!.TlsHandshakeDuration) : -1;
        var connect = -1;
        if (attribute)
        {
            var tcp = TenthSpan(connection!.TcpConnectDuration);
            var tunnel = TenthSpan(connection.UpstreamProxyConnectDuration);
            var parts = SumPresent(tcp, tunnel, ssl);
            connect = parts < 0 ? -1 : parts;
        }

        var send = TenthSpan(timing.RequestSendDuration);
        var wait = TenthSpan(timing.TimeToFirstByte);
        var clientRead = Math.Max(0, TenthSpan(timing.ClientRequestReadDuration));
        var connectionWait = Math.Max(0, TenthSpan(timing.ConnectionWaitDuration));
        var blocked = clientRead;
        if (attribute)
        {
            var setup = SumPresent(dns, connect);
            blocked += Math.Max(0, connectionWait - (setup < 0 ? 0 : setup));
        }
        else if (connectionWait > 0)
        {
            blocked += connectionWait;
        }

        var known = blocked + SumPresent(dns, connect, send, wait);
        if (known > total)
        {
            blocked = Math.Max(0, blocked - (known - total));
            known = blocked + SumPresent(dns, connect, send, wait);
        }

        var receive = Math.Max(0, total - known);
        return new Phases(
            FromTenth(blocked),
            FromTenth(dns),
            FromTenth(connect),
            FromTenth(ssl),
            FromTenth(send),
            FromTenth(wait),
            FromTenth(receive),
            reused);
    }

    public static double Sum(Phases phases)
    {
        var sum = phases.BlockedMs + phases.ReceiveMs;
        sum += Present(phases.DnsMs);
        sum += Present(phases.ConnectMs);
        sum += Present(phases.SendMs);
        sum += Present(phases.WaitMs);
        return Math.Round(sum, 1);
    }

    public static double HarValue(double? milliseconds) =>
        milliseconds is null or < 0 ? -1 : milliseconds.Value;

    private static double Present(double value) => value < 0 ? 0 : value;

    private static int SumPresent(params int[] values)
    {
        var any = false;
        var sum = 0;
        foreach (var value in values)
        {
            if (value < 0)
                continue;
            any = true;
            sum += value;
        }

        return any ? sum : -1;
    }

    private static int TenthSpan(TimeSpan? span) =>
        span is null ? -1 : Tenth(Math.Max(0, span.Value.TotalMilliseconds));

    private static int Tenth(double milliseconds) =>
        (int)Math.Round(Math.Max(0, milliseconds) * 10, MidpointRounding.AwayFromZero);

    private static double FromTenth(int tenths) => tenths < 0 ? -1 : tenths / 10.0;
}
