using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.Views;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
/// On-screen clocks convert with an explicit zone. Expected text is literal, so a UTC build agent
/// still fails when the conversion is skipped.
/// </summary>
[TestClass]
public partial class SessionDisplayClockTests
{
    private static readonly TimeZoneInfo Minus6 =
        TimeZoneInfo.CreateCustomTimeZone("UTC-6", TimeSpan.FromHours(-6), "UTC-6", "UTC-6");

    private static readonly TimeZoneInfo Plus2 =
        TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    private static readonly DateTimeOffset Sample = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [GeneratedRegex(@"-\d{4}")]
    private static partial Regex NegativeYear();

    [TestMethod]
    public void FormatAbsolute_ConvertsWithTheGivenOffset_AndTreatsUnspecifiedAsUtc()
    {
        Assert.AreEqual(
            "2026-01-01 21:04:05 -06:00",
            SessionDisplayFormat.FormatAbsolute(Sample, InspectorClock.Local, Minus6));
        Assert.AreEqual(
            "2026-01-02 03:04:05Z",
            SessionDisplayFormat.FormatAbsolute(Sample, InspectorClock.Utc, Minus6));
        Assert.AreEqual(
            "2026-01-01 21:04:05.000 -06:00",
            SessionDisplayFormat.FormatStartedFull(Sample, InspectorClock.Local, Minus6));
        Assert.AreEqual(
            "2026-01-02 03:04:05.000Z",
            SessionDisplayFormat.FormatStartedFull(Sample, InspectorClock.Utc, Minus6));

        var unspecified = SessionDisplayFormat.AsUtc(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified));
        var utc = SessionDisplayFormat.AsUtc(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Assert.AreEqual(
            SessionDisplayFormat.FormatAbsolute(utc, InspectorClock.Local, Minus6),
            SessionDisplayFormat.FormatAbsolute(unspecified, InspectorClock.Local, Minus6));
    }

    [TestMethod]
    public void FormatStarted_TodayFollowsTheSelectedClock_NotTheOtherOne()
    {
        var started = new DateTimeOffset(2026, 1, 2, 5, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 1, 2, 6, 0, 0, TimeSpan.Zero);
        using (CulturePin.Use("en-US"))
        {
            Assert.AreEqual("05:00:00Z", SessionDisplayFormat.FormatStarted(started, now, InspectorClock.Utc, Minus6));
            Assert.AreEqual("05:00Z", SessionDisplayFormat.FormatSince(started, now, InspectorClock.Utc, Minus6));
            Assert.AreEqual("Jan 1, 23:00:00", SessionDisplayFormat.FormatStarted(started, now, InspectorClock.Local, Minus6));
            Assert.AreEqual("Jan 1, 23:00", SessionDisplayFormat.FormatSince(started, now, InspectorClock.Local, Minus6));
        }
    }

    [TestMethod]
    public void FormatStarted_ReverseMidnight_LocalStaysOneDay_UtcCrosses()
    {
        var started = new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 1, 2, 0, 30, 0, TimeSpan.Zero);
        using (CulturePin.Use("en-US"))
        {
            Assert.AreEqual("01:30:00", SessionDisplayFormat.FormatStarted(started, now, InspectorClock.Local, Plus2));
            Assert.AreEqual("01:30", SessionDisplayFormat.FormatSince(started, now, InspectorClock.Local, Plus2));
            Assert.AreEqual("Jan 1, 23:30:00Z", SessionDisplayFormat.FormatStarted(started, now, InspectorClock.Utc, Plus2));
            Assert.AreEqual("Jan 1, 23:30Z", SessionDisplayFormat.FormatSince(started, now, InspectorClock.Utc, Plus2));
        }
    }

    [TestMethod]
    public void FormatStartedFull_UsesTheOffsetAtThatInstant_AcrossDaylightSaving()
    {
        var zone = FindZone("America/New_York", "Eastern Standard Time");
        if (zone is null)
        {
            Assert.Inconclusive("No US Eastern time zone is installed.");
            return;
        }

        var before = new DateTimeOffset(2026, 3, 8, 6, 59, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 3, 8, 7, 1, 0, TimeSpan.Zero);
        Assert.AreEqual(
            "2026-03-08 01:59:00.000 -05:00",
            SessionDisplayFormat.FormatStartedFull(before, InspectorClock.Local, zone));
        Assert.AreEqual(
            "2026-03-08 03:01:00.000 -04:00",
            SessionDisplayFormat.FormatStartedFull(after, InspectorClock.Local, zone));
    }

    [TestMethod]
    public void OutOfRangeInstants_DoNotThrow_AndDoNotShowANegativeYear()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        string[] texts =
        [
            SessionDisplayFormat.FormatStarted(DateTimeOffset.MinValue, now, InspectorClock.Local, Minus6),
            SessionDisplayFormat.FormatStarted(DateTimeOffset.MaxValue, now, InspectorClock.Local, Plus2),
            SessionDisplayFormat.FormatSince(DateTimeOffset.MinValue, now, InspectorClock.Local, Minus6),
            SessionDisplayFormat.FormatSince(DateTimeOffset.MaxValue, now, InspectorClock.Local, Plus2),
            SessionDisplayFormat.FormatAbsolute(DateTimeOffset.MinValue, InspectorClock.Local, Minus6),
            SessionDisplayFormat.FormatAbsolute(DateTimeOffset.MaxValue, InspectorClock.Local, Plus2),
            SessionDisplayFormat.FormatStartedFull(DateTimeOffset.MinValue, InspectorClock.Local, Minus6),
            SessionDisplayFormat.FormatStartedFull(DateTimeOffset.MaxValue, InspectorClock.Local, Plus2),
        ];

        foreach (var text in texts)
        {
            StringAssert.DoesNotMatch(text, NegativeYear());
            Assert.IsFalse(text.Contains("-0001", StringComparison.Ordinal), text);
        }

        // A zone that would step outside DateTime either throws (we show UTC with Z) or
        // returns the original instant (offset +00:00). Both stay inside year 1 and year 9999.
        var min = SessionDisplayFormat.FormatAbsolute(DateTimeOffset.MinValue, InspectorClock.Local, Minus6);
        var max = SessionDisplayFormat.FormatAbsolute(DateTimeOffset.MaxValue, InspectorClock.Local, Plus2);
        Assert.IsTrue(min is "0001-01-01 00:00:00Z" or "0001-01-01 00:00:00 +00:00", min);
        Assert.IsTrue(max is "9999-12-31 23:59:59Z" or "9999-12-31 23:59:59 +00:00", max);

        var learned = new DecryptFailureBypassEntry(
            "edge.example",
            strikes: 1,
            learnedAtUtc: DateTime.MinValue,
            expiresAtUtc: DateTime.UtcNow,
            bypassActive: true);
        var row = ExcludedHostsWindow.FormatLearnedDisplay(learned, InspectorClock.Local, Minus6);
        StringAssert.Contains(row, "edge.example");
        Assert.IsTrue(
            row.Contains("0001-01-01 00:00:00Z", StringComparison.Ordinal) ||
            row.Contains("0001-01-01 00:00:00 +00:00", StringComparison.Ordinal),
            row);
        Assert.IsFalse(row.Contains("-0001", StringComparison.Ordinal), row);

        var session = new SessionSnapshot { StartedUtc = DateTimeOffset.MinValue };
        Assert.IsFalse(session.StartedDisplay.Contains("-0001", StringComparison.Ordinal), session.StartedDisplay);
        Assert.IsFalse(session.StartedFullDisplay.Contains("-0001", StringComparison.Ordinal), session.StartedFullDisplay);
        session.StartedUtc = DateTimeOffset.MaxValue;
        Assert.IsFalse(session.StartedDisplay.Contains("-0001", StringComparison.Ordinal), session.StartedDisplay);
        var status = SessionSearch.BuildSessionCountText(1, 1, null, 0, DateTimeOffset.MinValue, now);
        StringAssert.Contains(status, "since ");
        Assert.IsFalse(status.Contains("-0001", StringComparison.Ordinal), status);
    }

    private static TimeZoneInfo? FindZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return null;
    }
}
