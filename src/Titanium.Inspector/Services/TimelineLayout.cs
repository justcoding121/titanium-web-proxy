namespace Titanium.Inspector.Services;

/// <summary>
/// Waterfall geometry for the Timeline window. Phases come from <see cref="SessionStatistics"/>
/// and are cached on the chart until the next refresh, so a frame only paints.
/// </summary>
public static class TimelineLayout
{
    public const int MaxRows = 2000;
    public const double RowHeight = 16;
    public const double RowGap = 4;
    public const double LabelWidth = 96;
    public const double MinTrack = 40;

    public enum RowKind
    {
        Phases,
        Tunnel,
        Estimated,
        InFlight,
        Plain,
    }

    public enum SegmentStyle
    {
        Phase,
        Plain,
        Hatched,
    }

    public readonly record struct Segment(
        SegmentStyle Style,
        SessionStatistics.Phase? Phase,
        double X,
        double Width,
        double Milliseconds);

    public sealed record Row(
        long Id,
        string Label,
        RowKind Kind,
        double Y,
        IReadOnlyList<Segment> Segments);

    public sealed record Chart(
        IReadOnlyList<Row> Rows,
        int TotalCount,
        bool Capped,
        double Width,
        double Height,
        double SpanMs);

    public static Chart Build(
        IReadOnlyList<SessionSnapshot> sessions,
        int totalCount,
        DateTimeOffset now,
        double width)
    {
        var shown = sessions;
        if (sessions.Count > MaxRows)
        {
            var start = sessions.Count - MaxRows;
            var trimmed = new SessionSnapshot[MaxRows];
            for (var i = 0; i < MaxRows; i++)
            {
                trimmed[i] = sessions[start + i];
            }

            shown = trimmed;
        }

        var capped = totalCount > MaxRows || sessions.Count > MaxRows;
        if (!double.IsFinite(width) || width < LabelWidth + MinTrack)
        {
            width = LabelWidth + MinTrack;
        }

        if (shown.Count == 0)
        {
            return new Chart([], Math.Max(0, totalCount), capped, width, 0, 1);
        }

        var origin = shown[0].StartedUtc;
        var end = origin;
        for (var i = 0; i < shown.Count; i++)
        {
            var session = shown[i];
            if (session.StartedUtc < origin)
            {
                origin = session.StartedUtc;
            }

            var sessionEnd = EndOf(session, now);
            if (sessionEnd > end)
            {
                end = sessionEnd;
            }
        }

        var span = (end - origin).TotalMilliseconds;
        if (span < 1)
        {
            span = 1;
        }

        var track = width - LabelWidth;
        var scale = track / span;
        var stride = RowHeight + RowGap;
        var rows = new Row[shown.Count];
        for (var i = 0; i < shown.Count; i++)
        {
            rows[i] = LayoutRow(shown[i], origin, now, scale, i * stride);
        }

        var height = shown.Count * stride - RowGap;
        return new Chart(rows, Math.Max(totalCount, shown.Count), capped, width, height, span);
    }

    public static long? HitTest(Chart chart, double x, double y)
    {
        _ = x;
        if (chart.Rows.Count == 0 || y < 0)
        {
            return null;
        }

        var index = (int)(y / (RowHeight + RowGap));
        if (index < 0 || index >= chart.Rows.Count)
        {
            return null;
        }

        return chart.Rows[index].Id;
    }

    private static Row LayoutRow(
        SessionSnapshot session,
        DateTimeOffset origin,
        DateTimeOffset now,
        double scale,
        double y)
    {
        var kind = KindOf(session);
        var segments = new List<Segment>(8);
        if (kind == RowKind.Phases)
        {
            kind = AddPhaseSegments(session, origin, scale, segments, kind);
        }

        if (kind != RowKind.Phases)
        {
            AddPlainSegment(session, origin, now, scale, kind, segments);
        }

        return new Row(session.Id, LabelFor(session), kind, y, segments);
    }

    private static RowKind AddPhaseSegments(
        SessionSnapshot session,
        DateTimeOffset origin,
        double scale,
        List<Segment> segments,
        RowKind kind)
    {
        var model = SessionStatistics.Build([session]);
        if (model.Kind != SessionStatistics.Kind.Phases || model.Phases.Count == 0)
        {
            return session.DurationMs is null ? RowKind.InFlight : RowKind.Plain;
        }

        var cursor = MillisecondsAfter(session.StartedUtc, origin);
        foreach (var phase in model.Phases)
        {
            var width = Math.Max(0, phase.Milliseconds) * scale;
            if (phase.Milliseconds > 0 && width < 1)
            {
                width = 1;
            }

            segments.Add(new Segment(
                SegmentStyle.Phase,
                phase.Phase,
                LabelWidth + (cursor * scale),
                width,
                phase.Milliseconds));
            cursor += Math.Max(0, phase.Milliseconds);
        }

        return kind;
    }

    private static void AddPlainSegment(
        SessionSnapshot session,
        DateTimeOffset origin,
        DateTimeOffset now,
        double scale,
        RowKind kind,
        List<Segment> segments)
    {
        var start = MillisecondsAfter(session.StartedUtc, origin);
        var duration = session.DurationMs is double known
            ? Math.Max(0, known)
            : Math.Max(0, (now - session.StartedUtc).TotalMilliseconds);
        var width = duration * scale;
        if (duration > 0 && width < 1)
        {
            width = 1;
        }

        var style = kind == RowKind.Estimated ? SegmentStyle.Hatched : SegmentStyle.Plain;
        segments.Add(new Segment(style, null, LabelWidth + (start * scale), width, duration));
    }

    private static RowKind KindOf(SessionSnapshot session)
    {
        if (session.IsTunnel)
        {
            return RowKind.Tunnel;
        }

        if (session.TimingEstimated)
        {
            return RowKind.Estimated;
        }

        if (session.DurationMs is null)
        {
            return RowKind.InFlight;
        }

        if (!session.TimingRecorded)
        {
            return RowKind.Plain;
        }

        return RowKind.Phases;
    }

    private static DateTimeOffset EndOf(SessionSnapshot session, DateTimeOffset now)
    {
        if (session.DurationMs is double duration)
        {
            return session.StartedUtc.AddMilliseconds(Math.Max(0, duration));
        }

        return now > session.StartedUtc ? now : session.StartedUtc;
    }

    private static double MillisecondsAfter(DateTimeOffset time, DateTimeOffset origin) =>
        Math.Max(0, (time - origin).TotalMilliseconds);

    private static string LabelFor(SessionSnapshot session)
    {
        var method = string.IsNullOrEmpty(session.Method) ? "?" : session.Method;
        if (session.StatusCode is int status)
        {
            return method + " " + status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return method;
    }
}
