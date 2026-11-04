using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Web.Proxy;

namespace Titanium.Cli.Tests;

/// <summary>
///     Keeps the metric catalog in <c>wiki/Metrics.md</c> equal to the instruments the core meter really declares.
///     Add an instrument and this fails until the page is regenerated with <c>UPDATE_TWP_METRICS_DOC=1</c>.
/// </summary>
[TestClass]
public class MetricCatalogTests
{
    private const string StartMarker = "<!-- metric-catalog:start -->";
    private const string EndMarker = "<!-- metric-catalog:end -->";

    [TestMethod]
    public void Catalog_ListsEveryCoreInstrument_AndMatchesWiki()
    {
        var catalog = BuildCatalog();
        var path = FindRepoFile(Path.Combine("wiki", "Metrics.md"));
        var page = File.ReadAllText(path).Replace("\r\n", "\n");
        var start = page.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = page.IndexOf(EndMarker, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start, "wiki/Metrics.md must contain the catalog markers.");

        var expected = page[..(start + StartMarker.Length)] + "\n" + catalog + page[end..];
        if (string.Equals(Environment.GetEnvironmentVariable("UPDATE_TWP_METRICS_DOC"), "1", StringComparison.Ordinal))
        {
            File.WriteAllText(path, expected);
            page = expected;
        }

        Assert.AreEqual(expected, page, "wiki/Metrics.md catalog is stale. Re-run with UPDATE_TWP_METRICS_DOC=1.");
    }

    [TestMethod]
    public void Catalog_ExposedFamilies_AreExactlyTheSelectedPrefixes()
    {
        foreach (var instrument in CoreInstruments())
        {
            var exposed = MetricsListener.IsSelected(instrument.Name);
            if (exposed)
            {
                var viaState = MetricsListener.InstrumentState.TryCreate(instrument, new MetricsListener(), out _);
                Assert.IsTrue(viaState, $"{instrument.Name} is selected but has an unsupported instrument shape.");
            }
        }

        var names = CoreInstruments().Select(i => i.Name).ToList();
        Assert.IsTrue(names.Contains("twp.proxy.requests"));
        Assert.IsTrue(names.Contains("twp.proxy.request.duration"));
        Assert.IsTrue(names.Contains("twp.certificates.cached"));
    }

    private static List<Instrument> CoreInstruments()
    {
        // The instruments live in an internal static class; running its constructor creates all of them.
        var type = typeof(ProxyServer).Assembly.GetType("Titanium.Web.Proxy.Diagnostics.ProxyMetrics", throwOnError: true)!;
        RuntimeHelpers.RunClassConstructor(type.TypeHandle);

        var found = new List<Instrument>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == MetricsListener.DefaultMeterName)
            {
                found.Add(instrument);
            }
        };
        listener.Start();
        return found.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    private static string BuildCatalog()
    {
        var sb = new StringBuilder();
        sb.Append("| Instrument | Kind | Unit | Prometheus series | Description |\n");
        sb.Append("|------------|------|------|-------------------|-------------|\n");
        foreach (var instrument in CoreInstruments())
        {
            var series = "not exposed";
            if (MetricsListener.IsSelected(instrument.Name) &&
                MetricsListener.InstrumentState.TryCreate(instrument, new MetricsListener(), out var state))
            {
                var name = PrometheusTextWriter.MetricName(state);
                series = state.Type == MetricsListener.MetricType.Histogram
                    ? $"`{name}` (histogram: `_bucket`, `_sum`, `_count`)"
                    : $"`{name}` ({state.Type.ToString().ToLowerInvariant()})";
            }

            sb.Append("| `").Append(instrument.Name).Append("` | ").Append(Kind(instrument)).Append(" | ")
                .Append(string.IsNullOrEmpty(instrument.Unit) ? "-" : "`" + instrument.Unit + "`").Append(" | ")
                .Append(series).Append(" | ")
                .Append((instrument.Description ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" "))
                .Append(" |\n");
        }

        return sb.ToString();
    }

    private static string Kind(Instrument instrument)
    {
        var name = instrument.GetType().Name;
        return name[..name.IndexOf('`')];
    }

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
    }
}
