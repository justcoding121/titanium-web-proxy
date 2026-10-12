using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class TimelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Phases_StackFromTheSessionStart()
    {
        var session = Session(7, 400);
        session.TimingRecorded = true;
        session.BlockedMs = 100;
        session.WaitMs = 300;

        var chart = TimelineLayout.Build([session], 1, Start.AddSeconds(1), TimelineLayout.LabelWidth + 400);
        var row = chart.Rows[0];
        Assert.AreEqual(TimelineLayout.RowKind.Phases, row.Kind);
        Assert.AreEqual(2, row.Segments.Count);
        Assert.AreEqual(SessionStatistics.Phase.Blocked, row.Segments[0].Phase);
        Assert.AreEqual(TimelineLayout.LabelWidth, row.Segments[0].X, 0.01);
        Assert.AreEqual(100, row.Segments[0].Width, 0.01);
        Assert.AreEqual(SessionStatistics.Phase.Wait, row.Segments[1].Phase);
        Assert.AreEqual(TimelineLayout.LabelWidth + 100, row.Segments[1].X, 0.01);
        Assert.AreEqual(300, row.Segments[1].Width, 0.01);
        Assert.AreEqual(7L, TimelineLayout.HitTest(chart, 4, 2));
        Assert.IsNull(TimelineLayout.HitTest(chart, 4, chart.Height + TimelineLayout.RowGap + 1));
        Assert.IsNull(TimelineLayout.HitTest(chart, 4, -1));
    }

    [TestMethod]
    public void Tunnel_IsOnePlainBar_EstimatedIsHatched_InFlightRunsToNow()
    {
        var tunnel = Session(1, 200);
        tunnel.IsTunnel = true;
        tunnel.TimingRecorded = true;
        tunnel.BlockedMs = 50;

        var estimated = Session(2, 50);
        estimated.StartedUtc = Start.AddMilliseconds(200);
        estimated.TimingEstimated = true;
        estimated.TimingRecorded = true;
        estimated.WaitMs = 50;

        var flight = Session(3, null);
        flight.StartedUtc = Start;

        var plain = TimelineLayout.Build([tunnel], 1, Start.AddSeconds(1), TimelineLayout.LabelWidth + 200);
        Assert.AreEqual(TimelineLayout.RowKind.Tunnel, plain.Rows[0].Kind);
        Assert.AreEqual(1, plain.Rows[0].Segments.Count);
        Assert.AreEqual(TimelineLayout.SegmentStyle.Plain, plain.Rows[0].Segments[0].Style);
        Assert.AreEqual(200, plain.Rows[0].Segments[0].Width, 0.01);

        var hatched = TimelineLayout.Build([estimated], 1, Start.AddSeconds(1), TimelineLayout.LabelWidth + 50);
        Assert.AreEqual(TimelineLayout.RowKind.Estimated, hatched.Rows[0].Kind);
        Assert.AreEqual(TimelineLayout.SegmentStyle.Hatched, hatched.Rows[0].Segments[0].Style);
        Assert.AreEqual(50, hatched.Rows[0].Segments[0].Width, 0.01);

        var now = Start.AddMilliseconds(500);
        var live = TimelineLayout.Build([flight], 1, now, TimelineLayout.LabelWidth + 500);
        Assert.AreEqual(TimelineLayout.RowKind.InFlight, live.Rows[0].Kind);
        Assert.AreEqual(TimelineLayout.SegmentStyle.Plain, live.Rows[0].Segments[0].Style);
        Assert.AreEqual(500, live.Rows[0].Segments[0].Width, 0.01);
    }

    [TestMethod]
    public void Open_CapsAtNewestTwoThousand_AndThrottlesUntilClose()
    {
        using var store = new SessionStore(new SessionStoreOptions { SpillBodiesToDisk = false });
        for (var id = 1; id <= TimelineLayout.MaxRows + 1; id++)
        {
            store.Add(Session(id, 10, Start.AddMilliseconds(id)));
        }

        long? selected = null;
        using var timeline = new TimelineViewModel(store, id => selected = id);
        var opened = Start.AddMinutes(1);
        timeline.Open(opened);
        Assert.IsTrue(timeline.IsSubscribed);
        Assert.AreEqual(TimelineLayout.MaxRows, timeline.Chart.Rows.Count);
        Assert.IsTrue(timeline.Chart.Capped);
        Assert.AreEqual(2, timeline.Chart.Rows[0].Id);
        Assert.AreEqual(TimelineLayout.MaxRows + 1, timeline.Chart.Rows[^1].Id);
        Assert.AreEqual(1, timeline.RefreshCount);

        store.Add(Session(9000, 10, Start.AddMinutes(2)));
        Assert.AreEqual(1, timeline.Notifications);
        Assert.IsFalse(timeline.Tick(opened.AddMilliseconds(10)));
        Assert.AreEqual(1, timeline.RefreshCount);
        Assert.AreEqual(2, timeline.Chart.Rows[0].Id);

        Assert.IsTrue(timeline.Tick(opened.Add(TimelineViewModel.RefreshInterval)));
        Assert.AreEqual(2, timeline.RefreshCount);
        Assert.AreEqual(9000, timeline.Chart.Rows[^1].Id);

        timeline.SelectAt(4, timeline.Chart.Rows[0].Y + 1);
        Assert.AreEqual(timeline.Chart.Rows[0].Id, selected);
        Assert.IsFalse(string.IsNullOrEmpty(timeline.Detail));

        var seen = timeline.Notifications;
        var rows = timeline.Chart.Rows.Count;
        timeline.Close();
        Assert.IsFalse(timeline.IsSubscribed);
        store.Add(Session(9001, 10, Start.AddMinutes(3)));
        Assert.AreEqual(seen, timeline.Notifications);
        Assert.IsFalse(timeline.Tick(opened.AddMinutes(1)));
        Assert.AreEqual(rows, timeline.Chart.Rows.Count);
    }

    [TestMethod]
    public void HiddenWindow_DoesNotRefresh_UntilShownAgain()
    {
        using var store = new SessionStore(new SessionStoreOptions { SpillBodiesToDisk = false });
        store.Add(Session(1, 10));
        using var timeline = new TimelineViewModel(store);
        var opened = Start.AddMinutes(1);
        timeline.Open(opened);
        timeline.SetVisible(false, opened);
        store.Add(Session(2, 10, Start.AddSeconds(1)));
        Assert.IsFalse(timeline.Tick(opened.AddSeconds(5)));
        Assert.AreEqual(1, timeline.Chart.Rows.Count);
        Assert.IsTrue(timeline.IsSubscribed);

        timeline.SetVisible(true, opened.AddSeconds(5));
        Assert.AreEqual(2, timeline.Chart.Rows.Count);
        timeline.SelectAt(0, -1);
        Assert.AreEqual("", timeline.Detail);
        var notes = timeline.Notifications;
        timeline.Open(opened.AddSeconds(6));
        store.Add(Session(3, 10, Start.AddSeconds(2)));
        Assert.AreEqual(notes + 1, timeline.Notifications);
        Assert.AreEqual("No sessions to chart.", new TimelineViewModel(store).Summary);
    }

    [TestMethod]
    public void Build_TrimsToTheCap_AndClampsShortBars()
    {
        using var store = new SessionStore(new SessionStoreOptions { SpillBodiesToDisk = false });
        Assert.AreEqual(0, store.CopyNewest(5).Length);
        Assert.AreEqual(0, store.CopyNewest(0).Length);

        var sessions = new SessionSnapshot[TimelineLayout.MaxRows + 1];
        for (var i = 0; i < sessions.Length; i++)
        {
            sessions[i] = Session(i + 1, 10, Start.AddMilliseconds(i));
        }

        var chart = TimelineLayout.Build(sessions, sessions.Length, Start.AddHours(1), 10);
        Assert.AreEqual(TimelineLayout.MaxRows, chart.Rows.Count);
        Assert.IsTrue(chart.Capped);
        Assert.AreEqual(2, chart.Rows[0].Id);
        Assert.IsTrue(chart.Width >= TimelineLayout.LabelWidth + TimelineLayout.MinTrack);
        Assert.IsNull(TimelineLayout.HitTest(TimelineLayout.Build([], 0, Start, 800), 0, 0));

        var tiny = Session(8, 10_000);
        tiny.TimingRecorded = true;
        tiny.BlockedMs = 0.1;
        var clamped = TimelineLayout.Build([tiny], 1, Start.AddSeconds(20), TimelineLayout.LabelWidth + TimelineLayout.MinTrack);
        Assert.AreEqual(1, clamped.Rows[0].Segments[0].Width, 0.01);

        var recorded = Session(9, 20);
        recorded.TimingRecorded = true;
        Assert.AreEqual(TimelineLayout.RowKind.Plain, TimelineLayout.Build([recorded], 1, Start, 800).Rows[0].Kind);

        var bare = new SessionSnapshot { Id = 4, Method = "", StartedUtc = Start, DurationMs = 5 };
        Assert.AreEqual("?", TimelineLayout.Build([bare], 1, Start, 800).Rows[0].Label);

        var future = Session(10, null, Start.AddMinutes(5));
        Assert.AreEqual(0, TimelineLayout.Build([future], 1, Start, 800).Rows[0].Segments[0].Width, 0.01);
    }

    [TestMethod]
    public void Chart_SelectedIds_SurvivesRebuildReorder()
    {
        var chart = new Titanium.Inspector.Views.TimelineChart();
        var sessions = new[] { Session(1, 10), Session(2, 10, Start.AddSeconds(1)) };
        chart.Model = TimelineLayout.Build(sessions, sessions.Length, Start.AddMinutes(1), 800);
        chart.SelectedIds = new HashSet<long> { 2 };
        Assert.IsTrue(chart.SelectedIds.Contains(2));

        // Rebuild that reorders rows must still highlight by id, not index.
        var rebuilt = new[] { Session(2, 10, Start.AddSeconds(1)), Session(1, 10), Session(3, 10, Start.AddSeconds(2)) };
        chart.Model = TimelineLayout.Build(rebuilt, rebuilt.Length, Start.AddMinutes(1), 800);
        Assert.IsTrue(chart.SelectedIds.Contains(2));
        Assert.IsFalse(chart.SelectedIds.Contains(99));

        chart.HoveredId = 1;
        Assert.AreEqual(1L, chart.HoveredId);
        chart.HoveredId = 1;
        Assert.AreEqual(1L, chart.HoveredId);
    }

    private static SessionSnapshot Session(long id, double? durationMs, DateTimeOffset? started = null) => new()
    {
        Id = id,
        Method = "GET",
        StatusCode = 200,
        Url = "https://ex.test/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StartedUtc = started ?? Start,
        DurationMs = durationMs,
    };
}
