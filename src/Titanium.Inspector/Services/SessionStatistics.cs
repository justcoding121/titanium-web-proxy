namespace Titanium.Inspector.Services;

/// <summary>
/// Facts for the Statistics inspect tab. Single sessions expose HAR phases; a multi-selection
/// exposes a bounded plain summary. Tunnels and sessions without recorded phases produce no rows.
/// </summary>
public static class SessionStatistics
{
    public const int MultiSelectCap = 2000;
    public const double BarTrackPixels = 200;

    public enum Kind
    {
        None,
        NotRecorded,
        Estimated,
        Phases,
        Multi,
    }

    public enum Phase
    {
        Blocked,
        Dns,
        Connect,
        Tls,
        Send,
        Wait,
        Receive,
    }

    public enum ConnectionNote
    {
        None,
        Reused,
        Prefetched,
    }

    public readonly record struct PhaseValue(Phase Phase, double Milliseconds);

    public readonly record struct StatusCount(int Class, int Count);

    public sealed record Model(
        Kind Kind,
        double? TotalMs,
        Phase? Slowest,
        IReadOnlyList<PhaseValue> Phases,
        ConnectionNote Note,
        int Attempts,
        int SessionCount,
        int ShownCount,
        bool Truncated,
        long TotalBytes,
        double? SlowestDurationMs,
        IReadOnlyList<StatusCount> StatusCounts);

    public static Model Build(IReadOnlyList<SessionSnapshot>? sessions)
    {
        if (sessions is null || sessions.Count == 0)
        {
            return Empty();
        }

        if (sessions.Count > 1)
        {
            return BuildMulti(sessions);
        }

        return BuildSingle(sessions[0]);
    }

    public static string PhaseKey(Phase phase) => phase switch
    {
        Phase.Blocked => "stats.phase.blocked",
        Phase.Dns => "stats.phase.dns",
        Phase.Connect => "stats.phase.connect",
        Phase.Tls => "stats.phase.tls",
        Phase.Send => "stats.phase.send",
        Phase.Wait => "stats.phase.wait",
        Phase.Receive => "stats.phase.receive",
        _ => "stats.phase.blocked",
    };

    private static Model BuildSingle(SessionSnapshot session)
    {
        if (session.IsTunnel || session.TimingEstimated || !session.TimingRecorded)
        {
            return Empty() with
            {
                Kind = session.TimingEstimated && !session.IsTunnel ? Kind.Estimated : Kind.NotRecorded,
                TotalMs = session.DurationMs,
            };
        }

        var phases = new List<PhaseValue>(7);
        Add(phases, Phase.Blocked, session.BlockedMs);
        Add(phases, Phase.Dns, session.DnsMs);
        Add(phases, Phase.Connect, session.ConnectMs);
        Add(phases, Phase.Tls, session.TlsMs);
        Add(phases, Phase.Send, session.SendMs);
        Add(phases, Phase.Wait, session.WaitMs);
        Add(phases, Phase.Receive, session.ReceiveMs);

        var setupAbsent = session.DnsMs is null && session.ConnectMs is null && session.TlsMs is null;
        var note = ConnectionNote.None;
        if (setupAbsent && session.ConnectionReused)
        {
            note = ConnectionNote.Reused;
        }
        else if (setupAbsent)
        {
            note = ConnectionNote.Prefetched;
        }

        Phase? slowest = null;
        var slowestMs = double.MinValue;
        foreach (var phase in phases)
        {
            if (phase.Milliseconds > slowestMs)
            {
                slowestMs = phase.Milliseconds;
                slowest = phase.Phase;
            }
        }

        return new Model(
            Kind.Phases,
            session.DurationMs,
            slowest,
            phases,
            note,
            session.TimingAttempts,
            1,
            1,
            false,
            session.BodySize ?? 0,
            session.DurationMs,
            []);
    }

    private static Model BuildMulti(IReadOnlyList<SessionSnapshot> sessions)
    {
        var shown = Math.Min(sessions.Count, MultiSelectCap);
        long bytes = 0;
        double? slowest = null;
        var classes = new int[6];
        for (var i = 0; i < shown; i++)
        {
            var session = sessions[i];
            bytes += session.BodySize ?? 0;
            if (session.DurationMs is double duration && (slowest is null || duration > slowest))
            {
                slowest = duration;
            }

            classes[StatusClass(session.StatusCode)]++;
        }

        var counts = new List<StatusCount>(6);
        for (var cls = 1; cls <= 5; cls++)
        {
            if (classes[cls] > 0)
            {
                counts.Add(new StatusCount(cls, classes[cls]));
            }
        }

        if (classes[0] > 0)
        {
            counts.Add(new StatusCount(0, classes[0]));
        }

        return new Model(
            Kind.Multi,
            null,
            null,
            [],
            ConnectionNote.None,
            0,
            sessions.Count,
            shown,
            sessions.Count > MultiSelectCap,
            bytes,
            slowest,
            counts);
    }

    private static int StatusClass(int? status)
    {
        if (status is null or < 100 or > 599)
        {
            return 0;
        }

        return status.Value / 100;
    }

    private static void Add(List<PhaseValue> phases, Phase phase, double? milliseconds)
    {
        if (milliseconds is double value && value >= 0)
        {
            phases.Add(new PhaseValue(phase, value));
        }
    }

    private static Model Empty() => new(
        Kind.None,
        null,
        null,
        [],
        ConnectionNote.None,
        0,
        0,
        0,
        false,
        0,
        null,
        []);
}
