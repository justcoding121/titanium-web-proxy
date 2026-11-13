using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// On-screen sizes and durations follow the PC regional format. Clipboard and protocol numbers stay invariant.
/// </summary>
[TestClass]
public class SessionDisplayQuantityTests
{
    [TestMethod]
    public void FormatByteSize_FollowsThePcDecimalMark_AndDoesNotChangeTheThreadCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            using (CulturePin.Use("en-US"))
            {
                Assert.AreEqual("1.5 KB", SessionDisplayFormat.FormatByteSize(1536));
                Assert.AreEqual("1.0 KB", SessionDisplayFormat.FormatByteSize(1024));
                Assert.AreEqual("1023 B", SessionDisplayFormat.FormatByteSize(1023));
                Assert.AreEqual("10 KB", SessionDisplayFormat.FormatByteSize(10 * 1024));
                Assert.AreEqual("2.0 MB", SessionDisplayFormat.FormatByteSize(2L * 1024 * 1024));
                Assert.AreEqual("", SessionDisplayFormat.FormatByteSize(null));
                Assert.AreEqual("", SessionDisplayFormat.FormatByteSize(-1));
                Assert.AreEqual(CultureInfo.GetCultureInfo("en-US"), CultureInfo.CurrentCulture);
            }

            using (CulturePin.Use("de-DE"))
            {
                Assert.AreEqual("1,5 KB", SessionDisplayFormat.FormatByteSize(1536));
                Assert.AreEqual("1,0 KB", SessionDisplayFormat.FormatByteSize(1024));
                Assert.AreEqual("1023 B", SessionDisplayFormat.FormatByteSize(1023));
                Assert.AreEqual("10 KB", SessionDisplayFormat.FormatByteSize(10 * 1024));
                Assert.AreEqual("2,0 MB", SessionDisplayFormat.FormatByteSize(2L * 1024 * 1024));
                Assert.AreEqual(CultureInfo.GetCultureInfo("de-DE"), CultureInfo.CurrentCulture);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }

        Assert.AreEqual(before, CultureInfo.CurrentCulture);
    }

    [TestMethod]
    public void FormatByteSize_PicksTheUnitFromTheRoundedValue()
    {
        using (CulturePin.Use("en-US"))
        {
            // 1023.999 KB used to stay in the KB branch and print "1024 KB".
            Assert.AreEqual("1.0 MB", SessionDisplayFormat.FormatByteSize(1_048_575));
            // 9.999 KB used to take the one-decimal branch and print "10.0 KB".
            Assert.AreEqual("10 KB", SessionDisplayFormat.FormatByteSize(10_239));
        }

        using (CulturePin.Use("de-DE"))
        {
            Assert.AreEqual("1,0 MB", SessionDisplayFormat.FormatByteSize(1_048_575));
            Assert.AreEqual("10 KB", SessionDisplayFormat.FormatByteSize(10_239));
        }
    }

    [TestMethod]
    public void FormatWholeMilliseconds_GroupsWithThePcCulture_AndRoundsHalvesAwayFromZero()
    {
        using (CulturePin.Use("en-US"))
        {
            Assert.AreEqual("12,000", SessionDisplayFormat.FormatWholeMilliseconds(12000));
            Assert.AreEqual("999", SessionDisplayFormat.FormatWholeMilliseconds(999.4));
            Assert.AreEqual("1,000", SessionDisplayFormat.FormatWholeMilliseconds(999.5));
            Assert.AreEqual("", SessionDisplayFormat.FormatWholeMilliseconds(null));
        }

        using (CulturePin.Use("de-DE"))
        {
            Assert.AreEqual("12.000", SessionDisplayFormat.FormatWholeMilliseconds(12000));
            Assert.AreEqual("999", SessionDisplayFormat.FormatWholeMilliseconds(999.4));
            Assert.AreEqual("1.000", SessionDisplayFormat.FormatWholeMilliseconds(999.5));
        }
    }

    [TestMethod]
    public void FormatFractionalMilliseconds_UsesThePcDecimalMark_AndDropsATrailingZero()
    {
        using (CulturePin.Use("en-US"))
        {
            Assert.AreEqual("12.5", SessionDisplayFormat.FormatFractionalMilliseconds(12.5));
            Assert.AreEqual("12", SessionDisplayFormat.FormatFractionalMilliseconds(12.04));
        }

        using (CulturePin.Use("de-DE"))
        {
            Assert.AreEqual("12,5", SessionDisplayFormat.FormatFractionalMilliseconds(12.5));
            Assert.AreEqual("12", SessionDisplayFormat.FormatFractionalMilliseconds(12.04));
        }
    }

    [TestMethod]
    public void OnScreenQuantitiesFollowThePc_ClipboardAndProtocolNumbersStayInvariant()
    {
        using (CulturePin.Use("de-DE"))
        {
            var session = new SessionSnapshot
            {
                Id = 1,
                StatusCode = 200,
                Url = "https://example.com/a",
                BodySize = 1536,
                DurationMs = 12000,
                TtfbMs = 999.5,
            };

            Assert.AreEqual("1,5 KB", session.BodySizeDisplay);
            Assert.AreEqual("1.5 KB", session.BodySizeClipboard);
            Assert.AreEqual("12.000", session.DurationDisplay);
            Assert.AreEqual("12000", session.DurationClipboard);
            Assert.AreEqual("1.000", session.TtfbDisplay);
            Assert.AreEqual("1000", session.TtfbClipboard);

            var table = SessionRequestCodegen.ToTable([session], out var written, out var truncated);
            Assert.AreEqual(1, written);
            Assert.IsFalse(truncated);
            StringAssert.Contains(table, "1\t");
            StringAssert.Contains(table, "\t200\t");
            Assert.IsFalse(table.Contains("1,5", StringComparison.Ordinal), table);
            Assert.IsFalse(table.Contains("12.000", StringComparison.Ordinal), table);

            Assert.IsTrue(SessionSearch.Matches(session, "status:200"));
            Assert.IsTrue(SessionSearch.Matches(session, "ids:1"));
        }
    }

    [TestMethod]
    public void DurationAndSizeSetters_RaiseTheDisplayProperties()
    {
        var names = new List<string?>();
        var session = new SessionSnapshot();
        session.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        session.DurationMs = 10;
        session.TtfbMs = 4;
        session.BodySize = 100;

        CollectionAssert.Contains(names, nameof(SessionSnapshot.DurationDisplay));
        CollectionAssert.Contains(names, nameof(SessionSnapshot.TtfbDisplay));
        CollectionAssert.Contains(names, nameof(SessionSnapshot.BodySizeDisplay));
    }
}
