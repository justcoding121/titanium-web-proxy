using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Titanium.Cli.Admin;

/// <summary>
///     Renders what a <see cref="MetricsListener" /> aggregated as Prometheus text exposition (format 0.0.4).
///     No third-party dependency.
///     <list type="bullet">
///         <item>Counters become <c>_total</c> counters; up-down counters and gauges become gauges.</item>
///         <item>Histograms become cumulative <c>_bucket{le}</c> series plus <c>_sum</c> and <c>_count</c>.</item>
///         <item>Dots become underscores and the OpenTelemetry unit becomes a suffix (<c>s</c> to <c>_seconds</c>, <c>By</c> to <c>_bytes</c>).</item>
///         <item>Only the tag keys in <see cref="MetricsListener.LabelNames" /> are written as labels.</item>
///     </list>
/// </summary>
internal static class PrometheusTextWriter
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Render(MetricsListener listener)
    {
        var sb = new StringBuilder(4096);
        Write(sb, listener);
        return sb.ToString();
    }

    public static void Write(StringBuilder sb, MetricsListener listener)
    {
        var instruments = listener.Instruments
            .Select(state => (State: state, Name: MetricName(state)))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal);

        foreach (var (state, name) in instruments)
        {
            WriteHeader(sb, state, name);
            var series = state.Cells
                .Select(pair => (Labels: FormatLabels(pair.Key), pair.Value))
                .OrderBy(entry => entry.Labels, StringComparer.Ordinal);
            foreach (var (labels, cell) in series)
            {
                if (state.Type == MetricsListener.MetricType.Histogram)
                {
                    WriteHistogram(sb, state, name, labels, cell);
                }
                else
                {
                    WriteScalar(sb, state, name, labels, cell);
                }
            }
        }
    }

    /// <summary>Prometheus family name for an instrument: dots to underscores, unit suffix, and <c>_total</c> for counters.</summary>
    public static string MetricName(MetricsListener.InstrumentState state)
    {
        var name = Sanitize(state.Name);
        var suffix = UnitSuffix(state.Unit);
        if (suffix is not null && !name.EndsWith("_" + suffix, StringComparison.Ordinal) &&
            !string.Equals(name, suffix, StringComparison.Ordinal))
        {
            name += "_" + suffix;
        }

        if (state.Type == MetricsListener.MetricType.Counter && !name.EndsWith("_total", StringComparison.Ordinal))
        {
            name += "_total";
        }

        return name;
    }

    private static string? UnitSuffix(string? unit) => unit switch
    {
        "s" => "seconds",
        "ms" => "milliseconds",
        "us" => "microseconds",
        "ns" => "nanoseconds",
        "By" => "bytes",
        "KiBy" => "kibibytes",
        "MiBy" => "mebibytes",
        "%" => "percent",
        _ => null,
    };

    private static string Sanitize(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var valid = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or ':' || (i > 0 && c is >= '0' and <= '9');
            if (!valid)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static void WriteHeader(StringBuilder sb, MetricsListener.InstrumentState state, string name)
    {
        if (!string.IsNullOrEmpty(state.Description))
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(state.Description)).Append('\n');
        }

        var type = state.Type switch
        {
            MetricsListener.MetricType.Counter => "counter",
            MetricsListener.MetricType.Histogram => "histogram",
            _ => "gauge",
        };
        sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    private static void WriteScalar(StringBuilder sb, MetricsListener.InstrumentState state, string name, string labels, MetricsListener.Cell cell)
    {
        sb.Append(name);
        if (labels.Length > 0)
        {
            sb.Append('{').Append(labels).Append('}');
        }

        sb.Append(' ');
        if (state.Integral)
        {
            sb.Append(System.Threading.Volatile.Read(ref cell.Long).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(FormatDouble(state.ReadDouble(cell)));
        }

        sb.Append('\n');
    }

    private static void WriteHistogram(StringBuilder sb, MetricsListener.InstrumentState state, string name, string labels, MetricsListener.Cell cell)
    {
        var buckets = cell.Buckets!;
        var prefix = labels.Length > 0 ? labels + "," : string.Empty;
        long cumulative = 0;
        for (var i = 0; i < buckets.Length; i++)
        {
            cumulative += System.Threading.Volatile.Read(ref buckets[i]);
            var le = i < state.Bounds.Length ? FormatDouble(state.Bounds[i]) : "+Inf";
            sb.Append(name).Append("_bucket{").Append(prefix).Append("le=\"").Append(le).Append("\"} ")
                .Append(cumulative.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        var suffix = labels.Length > 0 ? "{" + labels + "}" : string.Empty;
        sb.Append(name).Append("_sum").Append(suffix).Append(' ').Append(FormatDouble(state.ReadDouble(cell))).Append('\n');
        sb.Append(name).Append("_count").Append(suffix).Append(' ').Append(cumulative.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string FormatLabels(MetricsListener.SeriesKey key)
    {
        StringBuilder? sb = null;
        for (var slot = 0; slot < MetricsListener.LabelNames.Length; slot++)
        {
            if (key.Get(slot) is not { } value)
            {
                continue;
            }

            sb ??= new StringBuilder();
            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(MetricsListener.LabelNames[slot]).Append("=\"").Append(EscapeLabel(value)).Append('"');
        }

        return sb?.ToString() ?? string.Empty;
    }

    internal static string FormatDouble(double value)
    {
        if (double.IsPositiveInfinity(value))
        {
            return "+Inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Inf";
        }

        return double.IsNaN(value) ? "NaN" : value.ToString("R", CultureInfo.InvariantCulture);
    }

    internal static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeHelp(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
